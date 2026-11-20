using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A single-table write builds the seek source its WHERE is bound against once
/// per statement, not once per decision (<c>StatementContext.SeekSource</c>).
/// The memo lives for one statement's run, so a column renamed in place
/// (<c>sp_rename … 'COLUMN'</c> writes the new name into the existing
/// <c>HeapColumn</c>) between two writes must still seek the renamed column:
/// a memo shared across statements would keep the copied names and return no
/// rows. Debug runs also recompute the source on a power-of-two count of hits
/// (<c>Selection.AuditSeekSource</c>).
/// </summary>
[TestClass]
public sealed class MutationSeekSourceTests
{
    private static int Write(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        return command.ExecuteScalar();
    }

    private static string Contents(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            _ = reader.GetValues(values);
            rows.Add(string.Join(",", values));
        }
        return string.Join(";", rows);
    }

    private static DbConnection TwoIndexedColumns(Simulation sim)
    {
        var connection = sim.CreateOpenConnection();
        _ = Write(connection, """
            create table t (c0 int not null primary key, c1 int not null, w int null);
            create index ix1 on t (c1);
            insert t values (1, 10, 0), (2, 20, 0), (10, 1, 0), (20, 2, 0)
            """);
        return connection;
    }

    private static void SwapC0AndC1(DbConnection connection) =>
        _ = Write(connection, "exec sp_rename 't.c0', 'tmp', 'COLUMN'; exec sp_rename 't.c1', 'c0', 'COLUMN'; exec sp_rename 't.tmp', 'c1', 'COLUMN'");

    [TestMethod]
    public void ColumnsRenamedBetweenSeekingWrites_SeekTheRenamedColumn()
    {
        using var connection = TwoIndexedColumns(new Simulation());
        AreEqual(1, Write(connection, "update t set w = 5 where c0 = 2"));

        SwapC0AndC1(connection);

        // After the swap the column once called c1 (10, 20, 1, 2) answers to c0.
        AreEqual(1, Write(connection, "update t set w = 1 where c0 = 10"));
        AreEqual(1, Write(connection, "delete t where c0 = 20"));
        AreEqual("1,10,1;10,1,0;20,2,0", Contents(connection, "select c1, c0, w from t order by c1"));
    }

    [TestMethod]
    public void ColumnsRenamedBetweenSeekingWrites_MergeOnTheRenamedColumn()
    {
        using var connection = TwoIndexedColumns(new Simulation());
        AreEqual(1, Write(connection, "update t set w = 5 where c0 = 2"));

        SwapC0AndC1(connection);

        // The table's c0 is now the old c1, so 10 and 20 are the first two rows' old c1.
        AreEqual(1, Write(connection, "merge t using (values (10)) s(k) on t.c0 = s.k when matched then update set w = 77;"));
        AreEqual(77, Scalar(connection, "select w from t where c0 = 10"));
        AreEqual(1, Write(connection, "merge t using (values (20)) s(k) on t.c0 = s.k when matched then delete;"));
        AreEqual(3, Scalar(connection, "select count(*) from t"));
    }

    [TestMethod]
    public void RenameRolledBack_SeeksTheOriginalColumn()
    {
        using var connection = TwoIndexedColumns(new Simulation());
        AreEqual(1, Write(connection, "update t set w = 5 where c0 = 2"));

        _ = Write(connection, "begin tran");
        SwapC0AndC1(connection);
        AreEqual(1, Write(connection, "update t set w = 1 where c0 = 10"));
        AreEqual(1, Write(connection, "delete t where c0 = 20"));
        _ = Write(connection, "rollback");

        // Back to the original names: c0 = 10 is the row keyed 10, c0 = 20 the row keyed 20.
        AreEqual(1, Write(connection, "update t set w = 2 where c0 = 10"));
        AreEqual(1, Write(connection, "delete t where c0 = 20"));
        AreEqual("1,10,0;2,20,5;10,1,2", Contents(connection, "select c0, c1, w from t order by c0"));
    }

    [TestMethod]
    public void TableRenamed_WriteUnderTheNewName()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = Write(connection, "create table t (k int not null primary key, v int null); insert t values (4, 0), (5, 0), (6, 0)");
        AreEqual(1, Write(connection, "update t set v = 1 where k = 4"));

        _ = Write(connection, "exec sp_rename 't', 'u'");

        AreEqual(1, Write(connection, "update u set v = 2 where k = 5"));
        AreEqual(1, Write(connection, "update u set v = 3 where u.k = 6"));
        AreEqual(1, Write(connection, "delete u where k = 4"));
        AreEqual("5,2;6,3", Contents(connection, "select k, v from u order by k"));
    }

    [TestMethod]
    public void ColumnAddedOrDropped_SeeksTheRightColumn()
    {
        using var connection = TwoIndexedColumns(new Simulation());
        AreEqual(1, Write(connection, "update t set w = 5 where c1 = 20"));

        _ = Write(connection, "alter table t add extra int null");
        AreEqual(1, Write(connection, "update t set extra = 7 where c1 = 10"));
        _ = Write(connection, "alter table t drop column w");
        AreEqual(1, Write(connection, "update t set extra = 8 where c1 = 1"));
        AreEqual(1, Write(connection, "delete t where c1 = 20"));

        AreEqual("1,10,7;10,1,8;20,2,", Contents(connection, "select c0, c1, extra from t order by c0"));
    }

    [TestMethod]
    public void Truncate_ThenSeekingWrite()
    {
        using var connection = TwoIndexedColumns(new Simulation());
        AreEqual(1, Write(connection, "update t set w = 5 where c0 = 2"));

        _ = Write(connection, "truncate table t; insert t values (2, 22, 0), (3, 33, 0)");

        AreEqual(1, Write(connection, "update t set w = 6 where c0 = 2"));
        AreEqual(1, Write(connection, "delete t where c1 = 33"));
        AreEqual("2,22,6", Contents(connection, "select c0, c1, w from t"));
    }

    [TestMethod]
    public void SerializableWrite_FencesAndSeeksOneSource()
    {
        // The fence, the plan start, the seek and the wait mode each ask for the source; all of them over one.
        using var connection = TwoIndexedColumns(new Simulation());
        _ = Write(connection, "begin tran");
        AreEqual(1, Write(connection, "update t with (serializable) set w = 9 where c0 = 2"));
        AreEqual(1, Write(connection, "delete t with (holdlock) where c1 = 10"));
        _ = Write(connection, "commit");
        AreEqual("2,20,9;10,1,0;20,2,0", Contents(connection, "select c0, c1, w from t order by c0"));
    }

    [TestMethod]
    public void UpdateWithDivisionByZero_OverUniqueKeyMiss_StillRaisesNothing()
    {
        // MutationPlanStarts' error timing rides the same source: a unique-key singleton does not start the plan.
        using var connection = TwoIndexedColumns(new Simulation());
        AreEqual(0, Write(connection, "update t set w = 1/0 where c0 = 99"));
        _ = Throws<SimulatedSqlException>(() => Write(connection, "update t set w = 1/0 where w = 99"));
    }

    [TestMethod]
    public void ConcurrentKeyedUpdates_ShareTheSource()
    {
        var sim = new Simulation();
        const int Workers = 8;
        const int Rounds = 500;
        using (var setup = sim.CreateOpenConnection())
        {
            _ = Write(setup, "create table t (k int not null primary key, v int not null)");
            _ = Write(setup, $"insert t values {string.Join(",", Enumerable.Range(0, Workers).Select(i => $"({i}, -1)"))}");
        }

        _ = Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, worker =>
        {
            using var connection = sim.CreateOpenConnection();
            for (var round = 0; round < Rounds; round++)
                AreEqual(1, Write(connection, $"update t set v = {(worker * 1000) + round} where k = {worker}"));
        });

        using var reader = sim.CreateOpenConnection();
        for (var worker = 0; worker < Workers; worker++)
            AreEqual((worker * 1000) + Rounds - 1, Scalar(reader, $"select v from t where k = {worker}"));
    }
}
