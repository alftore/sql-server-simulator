using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The two guards the hot paths ask before a lookup — whether any definition
/// change holds a name in a schema, and whether any session holds a deleted
/// row's pre-image on a table — go on seeing what another session holds
/// through every empty, held and empty again, which a guard answering "empty"
/// from a stale count would not. Each probe runs under <c>SET LOCK_TIMEOUT 0</c>
/// so a block reads as Msg 1222.
/// </summary>
[TestClass]
public sealed class EmptinessGuardTests
{
    private static int Error(System.Data.Common.DbConnection connection, string sql)
    {
        try
        {
            _ = connection.CreateCommand(sql).ExecuteNonQuery();
            return 0;
        }
        catch (SimulatedSqlException ex)
        {
            return ex.Number;
        }
    }

    [TestMethod]
    public void ANameAChangeHolds_IsWaitedOnAgainAfterEveryEmptying()
    {
        var sim = new Simulation();
        using var holder = sim.CreateOpenConnection();
        using var prober = sim.CreateOpenConnection();
        _ = prober.CreateCommand("set lock_timeout 0").ExecuteNonQuery();

        // Nothing held: a name no object answers to is plainly missing.
        AreEqual(208, Error(prober, "select * from dbo.g1"));
        for (var round = 0; round < 3; round++)
        {
            _ = holder.CreateCommand("begin tran; create table dbo.g1 (a int)").ExecuteNonQuery();
            AreEqual(1222, Error(prober, "select * from dbo.g1"));
            AreEqual(1222, Error(prober, "select object_id(N'dbo.g1')"));
            _ = holder.CreateCommand("rollback").ExecuteNonQuery();
            AreEqual(208, Error(prober, "select * from dbo.g1"));
        }

        _ = holder.CreateCommand("begin tran; create table dbo.g1 (a int)").ExecuteNonQuery();
        AreEqual(1222, Error(prober, "select * from dbo.g1"));
        _ = holder.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(0, Error(prober, "select * from dbo.g1"));

        // A drop holds the name of an object that still resolves until it commits.
        _ = holder.CreateCommand("begin tran; drop table dbo.g1").ExecuteNonQuery();
        AreEqual(1222, Error(prober, "select * from dbo.g1"));
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(0, Error(prober, "select * from dbo.g1"));
    }

    [TestMethod]
    public void ADeletedKey_IsWaitedOnAgainAfterEveryEmptying()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (k int not null primary key, v int not null); insert t values (1, 1), (2, 2)");
        using var holder = sim.CreateOpenConnection();
        using var prober = sim.CreateOpenConnection();
        _ = prober.CreateCommand("set lock_timeout 0").ExecuteNonQuery();

        // Nothing deleted in flight: the key is taken.
        AreEqual(2627, Error(prober, "insert t values (1, 9)"));
        for (var round = 0; round < 3; round++)
        {
            _ = holder.CreateCommand("begin tran; delete t where k = 1").ExecuteNonQuery();
            AreEqual(1222, Error(prober, "insert t values (1, 9)"));
            AreEqual(1222, Error(prober, "update t set v = 9 where k = 1"));
            _ = holder.CreateCommand("rollback").ExecuteNonQuery();
            AreEqual(2627, Error(prober, "insert t values (1, 9)"));
        }

        _ = holder.CreateCommand("begin tran; delete t where k = 1").ExecuteNonQuery();
        AreEqual(1222, Error(prober, "insert t values (1, 9)"));
        _ = holder.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(0, Error(prober, "insert t values (1, 9)"));
    }
}
