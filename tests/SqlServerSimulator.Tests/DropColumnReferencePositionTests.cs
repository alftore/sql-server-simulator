using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What names a table's column from outside the table — a column-level
/// permission, a view, <c>sys.periods</c> — goes on naming the same column when
/// <c>ALTER TABLE … DROP COLUMN</c> moves it down a position, and when the drop
/// rolls back (probed 2026-11-25 against SQL Server 2019).
/// </summary>
[TestClass]
public sealed class DropColumnReferencePositionTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string DropX = "alter table t drop column x";
    private const string RolledBackDropX = "begin tran; alter table t drop column x; rollback";
    private const string AsU = "execute as user = 'u'; ";

    // The rows every result set holds, then the numbers of the errors raised.
    private static string Outcome(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(sql);
        var rows = new List<string>();
        try
        {
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    _ = reader.GetValues(values);
                    rows.Add(string.Join(",", values));
                }
            }
            while (reader.NextResult());
        }
        catch (SimulatedSqlException exception)
        {
            rows.Add("Msg " + string.Join("+", exception.Errors.Select(error => error.Number)));
        }
        return string.Join(";", rows);
    }

    private static Simulation After(params string[] statements)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(statements);
        return simulation;
    }

    // --- Column-level permissions ---

    private static void SameHoweverTheColumnLeft(string[] setup, string probe, string expected)
    {
        const string rows = "insert t (a, b, c) values (10, 20, 30)";
        string[] without = ["create table t (a int, b int, c int)", "create user u without login", .. setup, rows];
        string[] with = ["create table t (x int, a int, b int, c int)", "create user u without login", .. setup, rows];
        AreEqual(expected, Outcome(After(without), probe), "never had it");
        AreEqual(expected, Outcome(After([.. with, DropX]), probe), "dropped");
        AreEqual(expected, Outcome(After(["create table t (x int, a int, b int, c int)", "create user u without login", DropX, .. setup, rows]), probe), "dropped before");
        AreEqual(expected, Outcome(After(["create table t (x int, a int, b int, c int)", "create user u without login", RolledBackDropX, .. setup, rows, DropX]), probe), "rolled back, then dropped");
    }

    [TestMethod]
    [DataRow("grant select (a, b) on t to u", "select a, b from t", "10,20", DisplayName = "granted columns")]
    [DataRow("grant select (a, b) on t to u", "select c from t", "Msg 230", DisplayName = "column after the granted ones")]
    [DataRow("grant select (a, b) on t to u", "select count(*) from t", "Msg 230", DisplayName = "every column")]
    [DataRow("grant select (a, b) on t to u", "select 1 from t where c = 30", "Msg 230", DisplayName = "predicate column")]
    [DataRow("grant select on t to u|deny select (b) on t to u", "select a, c from t", "10,30", DisplayName = "undenied columns")]
    [DataRow("grant select on t to u|deny select (b) on t to u", "select b from t", "Msg 230", DisplayName = "denied column")]
    [DataRow("grant update (b) on t to u|grant select on t to u", "update t set b = 1; select b from t", "1", DisplayName = "update of the granted column")]
    [DataRow("grant update (b) on t to u", "update t set c = 1", "Msg 230", DisplayName = "update of the next column")]
    [DataRow("grant select (b) on t to u|grant update (c) on t to u", "update t set c = b", "", DisplayName = "update reading a granted column")]
    [DataRow("grant select (b) on t to u|grant delete on t to u", "delete t where b = 20", "", DisplayName = "delete reading a granted column")]
    [DataRow("grant select (b) on t to u|grant delete on t to u", "delete t where c = 30", "Msg 230", DisplayName = "delete reading an ungranted column")]
    [DataRow("grant select (a, b) on t to u|grant update (c) on t to u", "merge t using (values (10)) s(k) on t.a = s.k when matched then update set c = t.b;", "", DisplayName = "merge")]
    [DataRow("grant select (a) on t to u|grant update (c) on t to u", "merge t using (values (10)) s(k) on t.a = s.k when matched then update set c = t.b;", "Msg 230", DisplayName = "merge reading an ungranted column")]
    [DataRow("grant select (a, b) on t to u|revoke select (a) on t from u", "select a from t", "Msg 230", DisplayName = "revoked column")]
    [DataRow("grant select (a, b) on t to u|revoke select (a) on t from u", "select b from t", "20", DisplayName = "column left after a revoke")]
    [DataRow("grant select (b) on t to u", "select has_perms_by_name('t', 'OBJECT', 'SELECT', 'a', 'COLUMN'), has_perms_by_name('t', 'OBJECT', 'SELECT', 'b', 'COLUMN'), has_perms_by_name('t', 'OBJECT', 'SELECT', 'c', 'COLUMN')", "0,1,0", DisplayName = "HAS_PERMS_BY_NAME")]
    [DataRow("grant select (a, b, c) on t to u", "select has_perms_by_name('t', 'OBJECT', 'SELECT')", "1", DisplayName = "HAS_PERMS_BY_NAME on the object")]
    [DataRow("grant select (b) on t to u|grant update (c) on t to u", "select subentity_name, permission_name from fn_my_permissions('t', 'OBJECT') order by 1, 2", "b,SELECT;c,UPDATE", DisplayName = "fn_my_permissions")]
    [DataRow("grant select (b) on t to u", "select permissions(object_id('t'), 'a') & 1, permissions(object_id('t'), 'b') & 1, permissions(object_id('t'), 'c') & 1", "0,1,0", DisplayName = "PERMISSIONS of a column")]
    [DataRow("grant select (a, b, c) on t to u", "select permissions(object_id('t')) & 1", "1", DisplayName = "PERMISSIONS of the object")]
    [DataRow("grant select (b) on t to u", "select grantee, column_name from information_schema.column_privileges where table_name = 't'", "u,b", DisplayName = "COLUMN_PRIVILEGES")]
    public void ColumnPermission_FollowsTheColumn(string setup, string probe, string expected) =>
        SameHoweverTheColumnLeft(setup.Split('|'), AsU + probe, expected);

    [TestMethod]
    [DataRow("grant select (b) on t to u", "exec sp_helprotect 't'", DisplayName = "sp_helprotect")]
    [DataRow("grant select (b) on t to u|deny select (c) on t to u", "exec sp_helprotect 't'", DisplayName = "sp_helprotect with a denial")]
    [DataRow("grant select on t to u|deny select (b) on t to u", "exec sp_helprotect 't'", DisplayName = "sp_helprotect expanding an object grant")]
    [DataRow("grant select (b) on t to u", "select col_name(major_id, minor_id) from sys.database_permissions where major_id = object_id('t')", DisplayName = "sys.database_permissions")]
    public void ColumnPermission_ReportsTheColumn(string setup, string probe)
    {
        var expected = Outcome(After(["create table t (a int, b int, c int)", "create user u without login", .. setup.Split('|')]), probe);
        Contains("b", expected);
        SameHoweverTheColumnLeft(setup.Split('|'), probe, expected);
    }

    [TestMethod]
    public void ViewsColumnPermissions_AreListed()
    {
        var simulation = After("create table t (x int, a int, b int)", "create view v as select a, b from t", "create user u without login", "grant select (b) on v to u", DropX);
        AreEqual("u,v,b", Outcome(simulation, "select grantee, table_name, column_name from information_schema.column_privileges"));
    }

    [TestMethod]
    public void ColumnUnmask_FollowsTheColumn() =>
        SameHoweverTheColumnLeft(
            ["alter table t alter column b add masked with (function = 'default()')", "alter table t alter column c add masked with (function = 'default()')", "grant select on t to u", "grant unmask (b) on t to u"],
            AsU + "select a, b, c from t",
            "10,20,0");

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void ReferencesColumnGrant_FollowsTheColumn(string leave)
    {
        var simulation = After(
            "create table t (x int, a int not null unique, b int not null unique)", "create user u without login", "grant references (b) on t to u", "grant create table to u",
            "create schema s authorization u", leave);
        AreEqual("", Outcome(simulation, AsU + "create table s.child (b int references dbo.t (b))"));
        AreEqual("Msg 230+1088+1750", Outcome(simulation, AsU + "create table s.child2 (a int references dbo.t (a))"));
    }

    [TestMethod]
    public void DroppingTheColumn_DropsItsPermissions()
    {
        const string rows = "select minor_id, state from sys.database_permissions where major_id = object_id('t') order by 1";
        var simulation = After(
            "create table t (x int, a int, b int)", "create user u without login", "grant select (x, a) on t to u", "deny select (b) on t to u");
        AreEqual("1,G;2,G;3,D", Outcome(simulation, rows));
        AreEqual("2,G;3,D;1,G;2,G;3,D", Outcome(simulation, $"begin tran; alter table t drop column x; {rows}; rollback; {rows}"));
        _ = simulation.ExecuteNonQuery(DropX);
        AreEqual("2,G;3,D", Outcome(simulation, rows));

        // A column of the same name is another column.
        _ = simulation.ExecuteNonQuery("alter table t add x int");
        AreEqual("Msg 230", Outcome(simulation, AsU + "select x from t"));
    }

    [TestMethod]
    public void DroppingTheOnlyGrantedColumn_LeavesNoneAccessible()
    {
        var simulation = After("create table t (x int, a int)", "create user u without login", "grant select (x) on t to u", DropX);
        AreEqual("Msg 229", Outcome(simulation, AsU + "select a from t"));
    }

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void BrokenOwnershipChain_ChecksTheBaseColumn(string leave)
    {
        var simulation = After(
            "create table t (x int, a int, b int, c int)",
            "insert t (a, b, c) values (10, 20, 30)",
            "create user o without login",
            "create user u without login",
            "create view v as select a, b, c from t",
            "alter authorization on v to o",
            "grant select, update on v to u",
            "grant select (a, b) on t to u",
            "grant update (b) on t to u",
            leave);
        AreEqual("", Outcome(simulation, AsU + "update v set b = 21 where a = 10"));
        AreEqual("Msg 230", Outcome(simulation, AsU + "update v set c = 31 where a = 10"));
    }

    // --- Writes through a view ---

    private static readonly string[] ViewSetup =
    [
        "create table t (x int, a int, b int, c int)",
        "create view v as select a, b, c from t where b > 0 with check option",
        "create view v2 as select a as k, c as m from v where c > 0",
        "create view ve as select c, a + b as s, b from t",
        "create view vx as select b, x from t",
        "create view vs as select * from t",
        "create view vt as select top 1 a, c from t order by b desc",
        "create view vw as select a, c, row_number() over (order by b) as rn from t",
        "insert t (x, a, b, c) values (1, 10, 20, 30), (2, 11, 21, 31)",
    ];

    private const string Rows = "; select a, b, c from t order by a";

    [TestMethod]
    [DataRow("update v set c = 99 where a = 10", "10,20,99;11,21,31")]
    [DataRow("update v set c = 99 where b = 21", "10,20,30;11,21,99")]
    [DataRow("update v2 set m = 98 where k = 11", "10,20,30;11,21,98")]
    [DataRow("update v set b = -1 where a = 10", "Msg 550+3621")]
    [DataRow("update v2 set m = 5 where m = 30", "10,20,5;11,21,31")]
    [DataRow("insert v (a, b, c) values (12, 22, 32)", "10,20,30;11,21,31;12,22,32")]
    [DataRow("insert v values (12, 22, 32)", "10,20,30;11,21,31;12,22,32")]
    [DataRow("insert v (a, b, c) values (12, -22, 32)", "Msg 550+3621")]
    [DataRow("insert v2 (k, m) values (13, 33)", "Msg 550+3621")]
    [DataRow("insert v (c, b, a) select c + 10, b + 10, a + 10 from v", "10,20,30;11,21,31;20,30,40;21,31,41")]
    [DataRow("delete v where a = 10", "11,21,31")]
    [DataRow("delete v2 where m = 31", "10,20,30")]
    [DataRow("merge v using (values (10, 77), (14, 78)) s(a, c) on v.a = s.a when matched then update set c = s.c when not matched then insert (a, b, c) values (s.a, 1, s.c);", "10,20,77;11,21,31;14,1,78")]
    [DataRow("update ve set c = 5 where s = 30", "10,20,5;11,21,31")]
    [DataRow("update ve set s = 5", "Msg 4406")]
    [DataRow("delete ve where s = 32", "10,20,30")]
    [DataRow("update v set c = 5 output inserted.a, deleted.c, inserted.c where a = 11", "11,31,5;10,20,30;11,21,5")]
    [DataRow("delete v output deleted.a, deleted.b, deleted.c where a = 11", "11,21,31;10,20,30")]
    [DataRow("insert v (a, b, c) output inserted.c, inserted.b values (12, 22, 32)", "32,22;10,20,30;11,21,31;12,22,32")]
    [DataRow("update vt set c = 1", "10,20,30;11,21,1")]
    [DataRow("update vw set c = rn", "10,20,1;11,21,2")]
    [DataRow("delete vw where rn = 1", "11,21,31")]
    [DataRow("update v set c = u.n from v join (values (11, 70)) u(a, n) on u.a = v.a", "10,20,30;11,21,70")]
    [DataRow("delete v from v join (values (11)) u(a) on u.a = v.a", "10,20,30")]
    [DataRow("with q as (select a, c from v) update q set c = 8 where a = 10", "10,20,8;11,21,31")]
    [DataRow("with q as (select k, m from v2) delete q where m = 30", "11,21,31")]
    public void WriteThroughAView_ReachesTheSameColumns(string write, string expected)
    {
        var probe = write.TrimEnd(';') + (expected.StartsWith("Msg", StringComparison.Ordinal) ? ";" : Rows);
        foreach (var leave in (string[])["select 1", DropX, RolledBackDropX])
            AreEqual(expected, Outcome(After([.. ViewSetup, leave]), probe), leave);

        // A view written through once before the column went.
        foreach (var leave in (string[])[DropX, RolledBackDropX])
        {
            var simulation = After([.. ViewSetup, "update v set c = c; update v2 set m = m; update ve set c = c; update vt set c = c; update vw set c = c", leave]);
            AreEqual(expected, Outcome(simulation, probe), leave + " after a write");
        }
    }

    [TestMethod]
    public void WriteThroughAView_InTheTransactionDroppingTheColumn()
    {
        var simulation = After(ViewSetup);
        AreEqual(
            "10,20,1;11,21,2;10,20,3;11,21,31",
            Outcome(simulation, "begin tran; alter table t drop column x; update v set c = 1 where a = 10; update v2 set m = 2 where k = 11; select a, b, c from t order by a; rollback; update v set c = 3 where a = 10" + Rows));
        AreEqual("1,10,20,3;2,11,21,31", Outcome(simulation, "select x, a, b, c from t order by a"));
    }

    [TestMethod]
    [DataRow("update vx set b = 5")]
    [DataRow("insert vx (b) values (5)")]
    [DataRow("delete vx")]
    [DataRow("merge vx using (values (20)) s(b) on vx.b = s.b when matched then delete;")]
    public void WriteThroughAViewOfTheDroppedColumn_DoesNotBind(string write)
    {
        var simulation = After([.. ViewSetup, DropX]);
        AreEqual("Msg 207+4413", Outcome(simulation, write));
        AreEqual("10,20,30;11,21,31", Outcome(simulation, "select a, b, c from t order by a"));
    }

    [TestMethod]
    [DataRow("update vs set a = 5 where b = 20")]
    [DataRow("insert vs (a, b, c) values (1, 2, 3)")]
    [DataRow("delete vs")]
    public void WriteThroughAStarViewLeftShort_IsRefused(string write)
    {
        var simulation = After([.. ViewSetup, DropX]);
        AreEqual("Msg 4502", Outcome(simulation, write));
        AreEqual("10,20,30;11,21,31", Outcome(simulation, "select a, b, c from t order by a"));
    }

    [TestMethod]
    public void StarView_MapsItsNamesByPosition()
    {
        // vs's x, a, b, c read t's a, b, c, d.
        var simulation = After([.. ViewSetup, DropX, "alter table t add d int"]);
        AreEqual("10,20,30,;11,21,31,", Outcome(simulation, "select x, a, b, c from vs order by x"));
        AreEqual("10,20,30,;11,21,31,1000", Outcome(simulation, "update vs set c = 1000 where a = 21; select a, b, c, d from t order by a"));
        AreEqual("1,2,3,4;10,20,30,;11,21,31,1000", Outcome(simulation, "insert vs values (1, 2, 3, 4); select a, b, c, d from t order by a"));
    }

    [TestMethod]
    public void RenamedOrRecreatedTable_RebindsTheView()
    {
        var simulation = After([.. ViewSetup, "exec sp_rename 't.c', 'c9', 'COLUMN'"]);
        AreEqual("Msg 207+4413", Outcome(simulation, "update v set c = 1"));
        AreEqual("Msg 207+4413", Outcome(simulation, "delete v2"));
        _ = simulation.ExecuteNonQuery("drop table t");
        AreEqual("Msg 208+4413", Outcome(simulation, "update v set c = 1"));
        _ = simulation.ExecuteNonQuery("create table t (c int, b int, a int); insert t (a, b, c) values (10, 1, 7), (11, 0, 8)");
        AreEqual("10,1,55;11,0,8", Outcome(simulation, "update v set c = 55" + Rows));
        AreEqual("11,0,8", Outcome(simulation, "delete v2 where k = 10" + Rows));
    }

    [TestMethod]
    [DataRow("select * from v", "Msg 208+208")]
    [DataRow("update v set a = 1 where s = 1", "Msg 208+3621")]
    public void BindingAgainAfterADefinitionMoved_ReportsAnInlinedCallOnce(string sql, string expected)
    {
        var simulation = After("create table t (x int, a int)", "insert t (a) values (1)", "create table gone (a int)", "create function dbo.f() returns int as begin return (select max(a) from gone) end", "create view v as select a, dbo.f() as s from t", "drop table gone", DropX);
        AreEqual(expected, Outcome(simulation, sql));
    }

    [TestMethod]
    public void AlteredUpstreamView_RebindsTheViewOverIt()
    {
        var simulation = After([.. ViewSetup, "alter view v as select c as a, b, a as c from t"]);
        AreEqual("10,20,30;99,21,31", Outcome(simulation, "update v2 set m = 99 where k = 31" + Rows));
    }

    [TestMethod]
    public void CachedWriteThroughAView_FollowsTheColumn()
    {
        var simulation = After(ViewSetup);
        using var connection = simulation.CreateOpenConnection();
        for (var i = 0; i < 3; i++)
            _ = connection.CreateCommand("update v set c = c + 1 where a = 10").ExecuteNonQuery();
        _ = simulation.ExecuteNonQuery(DropX);
        for (var i = 0; i < 3; i++)
            _ = connection.CreateCommand("update v set c = c + 1 where a = 10").ExecuteNonQuery();
        AreEqual("10,20,36;11,21,31", Outcome(simulation, "select a, b, c from t order by a"));
    }

    [TestMethod]
    public void ProcedureWritingThroughAView_FollowsTheColumn()
    {
        var simulation = After([.. ViewSetup, "create procedure p as update v set c = c + 1 where a = 10", "exec p", DropX, "exec p"]);
        AreEqual("10,20,32;11,21,31", Outcome(simulation, "select a, b, c from t order by a"));
    }

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void InsteadOfTrigger_SeesTheViewsColumns(string leave)
    {
        var simulation = After([.. ViewSetup, "create table log (a int, b int, c int)", "create trigger tr on v2 instead of update as insert log select k, m, 0 from inserted", leave]);
        AreEqual("10,5,0", Outcome(simulation, "update v2 set m = 5 where k = 10; select * from log"));
    }

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void CursorOverAView_WritesTheSameColumns(string leave)
    {
        var simulation = After([.. ViewSetup, "create unique clustered index cx on t (a)", leave]);
        AreEqual(
            "10,20,5;11,21,31",
            Outcome(simulation, "declare @a int, @c int; declare k cursor for select a, c from v for update; open k; fetch k into @a, @c; update v set c = 5 where current of k; close k; deallocate k" + Rows));
    }

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void BulkInsertIntoAView_FillsTheSameColumns(string leave)
    {
        var simulation = new Simulation { OpenServerFile = (_, _) => new MemoryStream("12,22,32\n"u8.ToArray()) };
        simulation.ExecuteBatches([.. ViewSetup, leave]);
        AreEqual("10,20,30;11,21,31;12,22,32", Outcome(simulation, "bulk insert v from '/data/f.csv' with (fieldterminator = ',', rowterminator = '0x0a')" + Rows));
    }

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void JoinView_WritesTheSameColumns(string leave)
    {
        var simulation = After([.. ViewSetup, "create table r (a int primary key, n int); insert r values (10, 1), (11, 2)", "create view vj as select v.a, v.c, r.n from v join r on r.a = v.a", leave]);
        AreEqual("10,20,30;11,21,66", Outcome(simulation, "update vj set c = 66 where n = 2" + Rows));
    }

    [TestMethod]
    [DataRow("commit")]
    [DataRow("rollback")]
    public async Task WriteWaitingOutADrop_FollowsTheColumn(string ending)
    {
        var simulation = After(ViewSetup);
        using var dropper = simulation.CreateOpenConnection();
        using var writer = simulation.CreateOpenConnection();
        _ = writer.CreateCommand("update v set c = c").ExecuteNonQuery();
        _ = dropper.CreateCommand("begin tran; alter table t drop column x").ExecuteNonQuery();
        var write = await simulation.StartBlocked(writer, "update v set c = 99 where a = 10", TestContext.CancellationToken);
        _ = dropper.CreateCommand(ending).ExecuteNonQuery();
        _ = await write;
        AreEqual("10,20,99;11,21,31", Outcome(simulation, "select a, b, c from t order by a"));
    }

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void IndexedView_FollowsTheColumns(string leave)
    {
        var simulation = After(
            "create table dbo.t (x int, a int not null, b int not null, c int not null)",
            "insert t (a, b, c) values (10, 20, 30), (11, 21, 31)",
            "create view dbo.vi with schemabinding as select a, b, c from dbo.t where b > 0",
            "create unique clustered index cx on dbo.vi (c)",
            leave);
        AreEqual("Msg 2601+3621", Outcome(simulation, "insert vi (a, b, c) values (1, 1, 31)"));
        AreEqual("10,20,5;11,21,31", Outcome(simulation, "update vi set c = 5 where a = 10" + Rows));
    }

    // --- sys.periods ---

    [TestMethod]
    [DataRow("select 1")]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void Periods_NameTheColumnsById(string leave)
    {
        var simulation = After(
            "create table t (x int, id int primary key, s datetime2 generated always as row start not null, e datetime2 generated always as row end not null, period for system_time (s, e))",
            leave);
        AreEqual("s,e", Outcome(simulation, "select col_name(object_id, start_column_id), col_name(object_id, end_column_id) from sys.periods where object_id = object_id('t')"));
        AreEqual("3,4", Outcome(simulation, "select start_column_id, end_column_id from sys.periods where object_id = object_id('t')"));
    }
}
