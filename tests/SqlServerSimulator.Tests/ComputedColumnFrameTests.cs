using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A non-persisted computed column's expression reads its own columns through
/// a frame whose bindings last the enumeration. Only the binding is reused: a
/// value is computed on every read, a non-deterministic expression draws
/// afresh, an error surfaces on the row that raises it, and the expression
/// still reads its own table's row and database.
/// </summary>
[TestClass]
public sealed class ComputedColumnFrameTests
{
    [TestMethod]
    public void ComputedColumn_ReadManyTimesPerRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, a int, c as (a * 2)); insert t (id, a) values (1, 1), (2, 2), (3, 3)");
        using var connection = simulation.CreateOpenConnection();

        // The select list, a CASE and the WHERE each read c on every row.
        CollectionAssert.AreEqual(
            new object?[] { "2|a", "4|b", "6|b" },
            Extensions.FirstColumn(connection, "select concat(c, '|', case when c > 2 then 'b' else 'a' end) from t where c > 0 and c < 100 order by id"));

        // A self-join reads t1.c and t2.c from two sources over the same expression.
        CollectionAssert.AreEqual(
            new object?[] { 4, 10, 16 },
            Extensions.FirstColumn(connection, "select t1.c + t2.c + t1.c / 2 + t2.c / 2 - 2 from t t1 join t t2 on t1.id = t2.id and t1.c = t2.c order by t1.id"));
        CollectionAssert.AreEqual(
            new object?[] { 8 },
            Extensions.FirstColumn(connection, "select t1.c + t2.c from t t1 join t t2 on t1.id = 3 and t2.id = 1"));
    }

    [TestMethod]
    public void TriggerOverInsertedAndDeleted_ReadsEachSidesComputedColumn()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table tlog (note varchar(50));
            create table t (id int not null primary key, a int not null, c as a * 10);
            insert t (id, a) values (1, 4), (2, 5)
            """,
            """
            create trigger trg on t after update as
              insert tlog select concat('old=', d.c, ' new=', i.c, ' ', d.c + i.c) from deleted d join inserted i on d.id = i.id order by d.id
            """);
        _ = simulation.ExecuteNonQuery("update t set a = a + 3");
        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(
            new object?[] { "old=40 new=70 110", "old=50 new=80 130" },
            Extensions.FirstColumn(connection, "select note from tlog order by note"));
    }

    [TestMethod]
    public void ComputedColumnInAnotherDatabase_ReadsInItsOwnScope()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database other");
        _ = simulation.ExecuteNonQuery("use other; create table t (id int, a int)");
        simulation.ExecuteBatches(
            "use other",
            "create function dbo.f(@x int) returns varchar(50) as begin return concat(db_name(), '/', @x) end",
            "alter table t add c as dbo.f(a)");
        _ = simulation.ExecuteNonQuery("use other; insert t values (1, 7), (2, 8)");
        _ = simulation.ExecuteNonQuery("use simulated");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(
            new object?[] { "other/7|simulated", "other/8|simulated" },
            Extensions.FirstColumn(connection, "select concat(c, '|', db_name()) from other.dbo.t where c like 'other/%' order by id"));
    }

    [TestMethod]
    public void NonDeterministicComputedColumn_DrawsPerRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, c as (cast(newid() as nvarchar(36)))); insert t (id) values (1), (2), (3)");
        using var connection = simulation.CreateOpenConnection();

        var values = Extensions.FirstColumn(connection, "select c from t");
        HasCount(3, values.Distinct().ToList());

        // Two reads of c in one row draw twice as well.
        HasCount(3, Extensions.FirstColumn(connection, "select case when c = c then 1 else 0 end from t where c is not null"));
        AreEqual(0, Convert.ToInt32(Extensions.FirstColumn(connection, "select count(*) from t where c = c")[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void ComputedColumnError_RaisesOnTheSameRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, a int, d as (100 / a), o as (a + 2147483000)); insert t (id, a) values (1, 5), (2, 2), (3, 0), (4, 1000)");
        using var connection = simulation.CreateOpenConnection();

        // Row 3 divides by zero, and only row 3: the earlier rows answer.
        CollectionAssert.AreEqual(new object?[] { 20, 50 }, Extensions.FirstColumn(connection, "select d from t where id < 3 order by id"));
        _ = simulation.AssertSqlError("select d from t order by id", 8134);
        _ = simulation.AssertSqlError("select o from t order by id", 8115);
        CollectionAssert.AreEqual(new object?[] { 1 }, Extensions.FirstColumn(connection, "select id from t where id = 1 and d = 20 and o > 0"));
    }

    [TestMethod]
    public void ComputedColumnReadingAnotherComputedColumn_KeepsItsBehaviour()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, a int, c as (a * 2)); insert t (id, a) values (1, 4)");

        // Msg 1759 still refuses a computed column over a computed column.
        _ = simulation.AssertSqlError("alter table t add d as (c + 1)", 1759);
        AreEqual(8, simulation.ExecuteScalar<int>("select c from t"));
    }

    [TestMethod]
    public void LeftJoin_NullExtendedComputedColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table l (id int primary key); create table r (id int primary key, a int, c as (a + 1)); insert l values (1), (2); insert r (id, a) values (1, 9)");
        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(
            new object?[] { "10", "null" },
            Extensions.FirstColumn(connection, "select isnull(cast(r.c as varchar(10)), 'null') from l left join r on l.id = r.id order by l.id"));
        AreEqual("int", simulation.ExecuteScalar("select cast(sql_variant_property(isnull(r.c, 0), 'BaseType') as varchar(20)) from l left join r on l.id = r.id where l.id = 2"));
    }
}
