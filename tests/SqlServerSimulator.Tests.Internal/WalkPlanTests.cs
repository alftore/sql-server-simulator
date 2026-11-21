using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which <c>SELECT</c> statements of a batch compiling for the first time run
/// on the batch compile walk's parse of them rather than a second parse,
/// observed through <see cref="Simulation.WalkPlanReplays"/>: a plain query
/// does, while one whose parse sends what a compile sends, reads what belongs
/// to the session, or runs after the batch changed what it would parse under,
/// parses as it runs. Whether the run reports what a parse of its own would is
/// the public suites' to show.
/// </summary>
[TestClass]
public sealed class WalkPlanTests
{
    private const string Setup = """
        create table t (id int primary key, v int not null default 0);
        insert t values (1, 1), (2, 2);
        create table u (id int primary key, t_id int not null);
        insert u values (10, 1);
        """;

    private static void Execute(DbConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }

    /// <summary>
    /// Runs <see cref="Setup"/> and <paramref name="before"/> (batches split on
    /// <c>go</c> lines), then <paramref name="text"/> once, returning the walk's
    /// parses its run replayed. In a Debug build, no write the walk attempted
    /// may have been turned away at its split point: the pre-scan admits only
    /// what reaches a plan.
    /// </summary>
    private static long WalkReplays(string text, string before = "")
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        foreach (var batch in before.Split("\ngo\n", StringSplitOptions.RemoveEmptyEntries))
            Execute(connection, batch);
        var replays = simulation.WalkPlanReplays;
#if DEBUG
        var declines = simulation.WalkSplitDeclines.Values.Sum();
#endif
        Execute(connection, text);
#if DEBUG
        AreEqual(declines, simulation.WalkSplitDeclines.Values.Sum(), "A write the pre-scan admitted declined at its split point.");
#endif
        return simulation.WalkPlanReplays - replays;
    }

    [TestMethod]
    [DataRow("select v from t where id = 1")]
    [DataRow("select t.v, u.id from t join u on u.t_id = t.id where t.id = 1 order by u.id")]
    [DataRow("select count(*) from t where v in (select t_id from u)")]
    [DataRow("select v from t where id = 1; select id from u")]
    public void APlainQuery_RunsOnTheWalksParse(string text) => AreEqual(text.Split(';').Length, WalkReplays(text));

    [TestMethod]
    [DataRow("select t.v from t inner hash join u on u.t_id = t.id", DisplayName = "A join hint, whose warning a compile sends")]
    [DataRow("select v from t with (nolock)", DisplayName = "A table hint")]
    [DataRow("select v from t option (maxdop 1)", DisplayName = "A query hint")]
    [DataRow("select dbo.f(v) from t", DisplayName = "A user function, which a compile inlines")]
    [DataRow("select * from #s", DisplayName = "A temporary table")]
    [DataRow("select 1", DisplayName = "A query reading no table")]
    [DataRow("select v into t2 from t", DisplayName = "SELECT … INTO")]
    public void AQueryACompileSettlesMore_ParsesAsItRuns(string text) =>
        AreEqual(0, WalkReplays(text, "create function f (@v int) returns int as begin return @v end\ngo\ncreate table #s (a int)"));

    [TestMethod]
    [DataRow("set transaction isolation level serializable; select v from t", DisplayName = "An isolation level the cache stays out of")]
    [DataRow("create table w (a int); select v from t", DisplayName = "A schema change ahead of it")]
    [DataRow("set ansi_nulls off; select v from t where v = null", DisplayName = "A setting the cache key took")]
    public void AQueryTheBatchChangedTheGroundUnder_ParsesAsItRuns(string text) => AreEqual(0, WalkReplays(text));

    /// <summary>
    /// The walk's parse serves the statement's first run, which files it in the
    /// plan cache as its own parse would have been, so the loop's later
    /// iterations replay the cached plan.
    /// </summary>
    [TestMethod]
    public void AQueryInALoop_RunsOnTheWalksParseOnce_ThenOnTheCachedPlan()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        var (replays, hits) = (simulation.WalkPlanReplays, simulation.SelectPlanHits);
        Execute(connection, "declare @i int = 0; while @i < 3 begin select v from t where id = 1; set @i += 1 end");
        AreEqual((1, 2), (simulation.WalkPlanReplays - replays, simulation.SelectPlanHits - hits));
    }

    [TestMethod]
    [DataRow("insert t values (3, 3)")]
    [DataRow("insert into dbo.t (id, v) values (3, 3), (4, default)")]
    [DataRow("insert t (id, v) select id + 10, v from t where v > (select min(t_id) from u)")]
    [DataRow("update t set v = v + 1 where id = 1")]
    [DataRow("update dbo.t set v = default where id = 1")]
    [DataRow("delete from u where id = 10")]
    [DataRow("delete u")]
    [DataRow("insert t values (3, 3); update t set v = 0 where id = 3; delete t where id = 3")]
    [DataRow("update t set v = 0 output inserted.id")]
    [DataRow("insert t (id, v) output inserted.id, inserted.v select id + 10, v from t")]
    [DataRow("delete u output deleted.* where id = 10")]
    public void APlainWrite_RunsOnTheWalksParse(string text) => AreEqual(text.Split(';').Length, WalkReplays(text));

    /// <summary>
    /// EF Core's writes, each with a client <c>OUTPUT</c> clause (captured from
    /// EF Core 10's SqlServer provider), run on the walk's parse; the
    /// <c>SET</c>s ahead of them have no plan to run on.
    /// </summary>
    [TestMethod]
    [DataRow("SET IMPLICIT_TRANSACTIONS OFF;\nSET NOCOUNT ON;\nINSERT INTO [t] ([id], [v])\nOUTPUT INSERTED.[id]\nVALUES (3, 3);\n", 1, DisplayName = "INSERT … OUTPUT … VALUES")]
    [DataRow("SET NOCOUNT ON;\nUPDATE [t] SET [v] = 5\nOUTPUT 1\nWHERE [id] = 1;\nUPDATE [u] SET [t_id] = 2\nOUTPUT 1\nWHERE [id] = 10;\n", 2, DisplayName = "UPDATE … OUTPUT 1 WHERE, twice")]
    [DataRow("SET IMPLICIT_TRANSACTIONS OFF;\nSET NOCOUNT ON;\nDELETE FROM [u]\nOUTPUT 1\nWHERE [id] = 10;\n", 1, DisplayName = "DELETE … OUTPUT 1 WHERE")]
    public void EfCoresWrites_RunOnTheWalksParse(string text, int writes) => AreEqual(writes, WalkReplays(text));

    [TestMethod]
    [DataRow("insert t with (tablock) values (3, 3)", DisplayName = "A table hint")]
    [DataRow("update top (1) t set v = 0", DisplayName = "TOP")]
    [DataRow("update t set v = 0 from t join u on u.t_id = t.id", DisplayName = "A joined UPDATE")]
    [DataRow("delete t from t join u on u.t_id = t.id", DisplayName = "A joined DELETE")]
    [DataRow("insert t select 5, 5", DisplayName = "A FROM-less source, evaluated as it parses")]
    [DataRow("insert t exec p", DisplayName = "An executed source")]
    [DataRow("insert #s values (1)", DisplayName = "A temporary table")]
    [DataRow("insert t values (dbo.f(3), 3)", DisplayName = "A user function")]
    [DataRow("insert vt values (3, 3)", DisplayName = "A view")]
    [DataRow("update vt set v = 0 where id = 1", DisplayName = "A view's UPDATE")]
    [DataRow("delete vt where id = 1", DisplayName = "A view's DELETE")]
    [DataRow("merge t using u on u.t_id = t.id when matched then delete;", DisplayName = "MERGE")]
    public void AWriteACompileSettlesMore_ParsesAsItRuns(string text) =>
        AreEqual(0, WalkReplays(text, "create function f (@v int) returns int as begin return @v end\ngo\ncreate table #s (a int)\ngo\ncreate procedure p as select 5, 5\ngo\ncreate view vt as select id, v from t"));

    /// <summary>
    /// An <c>OUTPUT</c> clause the walk can't settle as the run would keeps
    /// its write parsing as it runs: one writing a table, whose target the
    /// plan checks for itself, and a client one on a table with a trigger,
    /// enabled or not, whose parse settles Msg 334 from the trigger's state.
    /// </summary>
    [TestMethod]
    [DataRow("declare @o table (id int); update t set v = 0 output inserted.id into @o", DisplayName = "OUTPUT … INTO a table variable")]
    [DataRow("insert t output inserted.id, inserted.v into u values (5, 5)", DisplayName = "OUTPUT … INTO a table")]
    [DataRow("update g set v = 1 output inserted.v", DisplayName = "A client OUTPUT on a table with a disabled trigger")]
    [DataRow("insert g output inserted.id values (2, 2)", DisplayName = "A client INSERT … OUTPUT on a table with a disabled trigger")]
    public void AnOutputACompileSettlesMore_ParsesAsItRuns(string text) =>
        AreEqual(0, WalkReplays(text, "create table g (id int primary key, v int)\ngo\ncreate trigger g_t on g after insert, update as set nocount on\ngo\ndisable trigger g_t on g"));

    /// <summary>
    /// A <c>DROP</c> the walk meets, even in a branch the run never takes,
    /// keeps every later statement of the batch parsing as it runs, which is
    /// how <c>DmlPlanReplayTests</c> sets the walk's parse aside to compare
    /// the run on it with the run on its own.
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 0 drop table if exists nothing; update t set v = 0 output inserted.id", DisplayName = "UPDATE … OUTPUT")]
    [DataRow("if 1 = 0 drop table if exists nothing; insert t output inserted.id values (3, 3)", DisplayName = "INSERT … OUTPUT")]
    public void AWriteBehindDdlTheWalkMet_ParsesAsItRuns(string text) => AreEqual(0, WalkReplays(text));

#if DEBUG
    /// <summary>
    /// A write the pre-scan admits whose split point declines its plan — an
    /// <c>UPDATE</c> assigning a variable — is turned away there, parsing the
    /// walk's way, and counted.
    /// </summary>
    [TestMethod]
    public void AWriteItsSplitPointDeclines_IsCounted()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        var replays = simulation.WalkPlanReplays;
        Execute(connection, "declare @x int; update t set @x = v where id = 1");
        AreEqual(0, simulation.WalkPlanReplays - replays);
        AreEqual(1, simulation.WalkSplitDeclines.GetValueOrDefault(Parser.Keyword.Update));
    }
#endif

    /// <summary>
    /// A write the walk parsed for the run reads the session's state as it
    /// runs, after the statements ahead of it changed it, as its own parse
    /// would have left it to.
    /// </summary>
    [TestMethod]
    public void AWriteOnTheWalksParse_RunsUnderWhatTheBatchSetAheadOfIt()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, "create table d (id int identity primary key, v int not null)");
        var replays = simulation.WalkPlanReplays;
        Execute(connection, "set identity_insert d on; insert d (id, v) values (5, 5); set identity_insert d off");
        AreEqual(1, simulation.WalkPlanReplays - replays);
        using var command = connection.CreateCommand();
        command.CommandText = "select id from d";
        AreEqual(5, command.ExecuteScalar());
    }

    /// <summary>
    /// A write in a loop runs on the walk's parse once, which files it in the
    /// plan cache, so the loop's later iterations replay the cached plan.
    /// </summary>
    [TestMethod]
    public void AWriteInALoop_RunsOnTheWalksParseOnce_ThenOnTheCachedPlan()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        var (replays, hits) = (simulation.WalkPlanReplays, simulation.DmlPlanHits);
        Execute(connection, "declare @i int = 0; while @i < 3 begin update t set v = v + 1 where id = 1; set @i += 1 end");
        AreEqual((1, 2), (simulation.WalkPlanReplays - replays, simulation.DmlPlanHits - hits));
    }

    /// <summary>
    /// The walk parses a query in a branch the run doesn't take, which the run
    /// never files in the plan cache, so a later run taking the branch parses it
    /// as it would have.
    /// </summary>
    [TestMethod]
    public void AQueryInABranchNotTaken_IsLeftToTheRunThatTakesIt()
    {
        const string Batch = "declare @x int = (select count(*) from u); if @x > 1 select v from t where id = 2";
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        var (replays, recordings) = (simulation.WalkPlanReplays, simulation.SelectPlanRecordings);
        Execute(connection, Batch);
        AreEqual(0, simulation.WalkPlanReplays - replays);
        AreEqual(0, simulation.SelectPlanRecordings - recordings);

        Execute(connection, "insert u values (11, 2)");
        Execute(connection, Batch);
        AreEqual(1, simulation.SelectPlanRecordings - recordings);
    }
}
