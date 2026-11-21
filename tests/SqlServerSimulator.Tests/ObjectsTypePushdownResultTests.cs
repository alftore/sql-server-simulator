using System.Data.Common;
using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Result parity for a <c>type</c> filter on <c>sys.objects</c>, which on a cold
/// row cache reads through a generator that skips the system rows a comparand
/// can't match. Each query is run against a cold cache and again after the
/// whole view was read (a cache hit), and its rows, in order, must equal the
/// same query over <c>(select * from sys.objects) o</c>, which no pushdown reaches
/// — in the user database, <c>msdb</c> (seeded on first read), <c>tempdb</c>, and
/// a case-sensitive and a binary collation. The path itself is pinned in
/// <c>CatalogPushdownTests</c>.
/// </summary>
[TestClass]
public sealed class ObjectsTypePushdownResultTests
{
    private const string Objects = """
        create table t1 (a int not null primary key, b int not null constraint df default 0, constraint ck check (b >= 0));
        create table t2 (a int not null primary key, j json);
        create json index ji on t2 (j);
        create type tt as table (x int);
        create queue q;
        """;

    private const string ChangeTracking = "alter database current set change_tracking = on; alter table t1 enable change_tracking";

    private static readonly string[] Databases = ["", "msdb.", "tempdb.", "cs.", "bin."];

    private static DbConnection Prepare(Simulation sim)
    {
        var connection = sim.CreateDbConnection();
        connection.Open();
        Execute(connection, "create database cs collate Latin1_General_CS_AS");
        Execute(connection, "create database bin collate Latin1_General_BIN2");
        Execute(connection, Objects);
        Execute(connection, ChangeTracking);
        foreach (var name in new[] { "cs", "bin" })
        {
            Execute(connection, $"use {name}");
            Execute(connection, Objects);
            Execute(connection, ChangeTracking);
        }

        Execute(connection, "use simulated");
        Execute(connection, "create table #temp (a int)");
        return connection;
    }

    private static void Execute(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = command.ExecuteNonQuery();
    }

    // The rows of the first result set, each as its cells joined, or the error.
    private static string Run(DbConnection connection, string commandText)
    {
        var rows = new List<string>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i =>
                    reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            }
        }
        catch (DbException e)
        {
            rows.Add($"error {(e as SimulatedSqlException)?.Number}: {e.Message}");
        }

        return string.Join("\n", rows);
    }

    [TestMethod]
    [DataRow("", "type = 'U'")]
    [DataRow("", "type = 'U '")]
    [DataRow("", "type = 'u'")]
    [DataRow("", "type = N'U'")]
    [DataRow("", "type = 'S'")]
    [DataRow("", "type = 'S '")]
    [DataRow("", "type = 's'")]
    [DataRow("", "type = 'IT'")]
    [DataRow("", "type = 'it'")]
    [DataRow("", "type = 'SQ'")]
    [DataRow("", "type = 'sq'")]
    [DataRow("", "type = 'TT'")]
    [DataRow("", "type = 'PK'")]
    [DataRow("", "type = 'D'")]
    [DataRow("", "type = 'C'")]
    [DataRow("", "type = 'X'")]
    [DataRow("", "type = ''")]
    [DataRow("", "type = ' '")]
    [DataRow("", "type = 'USR'")]
    [DataRow("", "type = 'S' collate Latin1_General_BIN")]
    [DataRow("", "type = 's' collate Latin1_General_BIN")]
    [DataRow("", "type = 'u' collate Latin1_General_CS_AS")]
    [DataRow("", "type = 'U' collate Turkish_CI_AS")]
    [DataRow("", "type = 'I' + 'T'")]
    [DataRow("", "type = null")]
    [DataRow("", "type is null")]
    [DataRow("", "type <> 'U'")]
    [DataRow("", "not type = 'U'")]
    [DataRow("", "type = 'U' or type = 'S'")]
    [DataRow("", "type like 'U%'")]
    [DataRow("", "type like 'S%'")]
    [DataRow("", "type like '_Q'")]
    [DataRow("", "type in ('S', 'U')")]
    [DataRow("", "type in ('X', 'Y')")]
    [DataRow("", "type in ('U', null)")]
    [DataRow("", "type = 'U' and is_ms_shipped = 0")]
    [DataRow("", "type = 'S' and name like 'sys%'")]
    [DataRow("", "type = 1")]
    [DataRow("", "type = cast(1 as bigint)")]
    [DataRow("", "type = 0x55")]
    [DataRow("", "type = cast('U' as sql_variant)")]
    [DataRow("declare @p nvarchar(10) = N'S';", "type = @p")]
    [DataRow("declare @p nvarchar(10) = N'U';", "type = @p")]
    [DataRow("declare @p varchar(10) = 'u';", "type = @p")]
    [DataRow("declare @p char(5) = 'SQ';", "type = @p")]
    [DataRow("declare @p nvarchar(10) = null;", "type = @p")]
    [DataRow("declare @p sysname = N'IT';", "type = @p")]
    public void TypeFilter_ReturnsWhatTheUnfilteredViewDoes(string prelude, string predicate)
    {
        foreach (var database in Databases)
        {
            var connection = Prepare(new Simulation());
            var expected = Run(connection, $"{prelude} select * from (select * from {database}sys.objects) o where {predicate}");
            AreEqual(expected, Run(connection, $"{prelude} select * from {database}sys.objects where {predicate}"), $"{database} cold: {predicate}");
            _ = Run(connection, $"select count(*) from {database}sys.objects");
            AreEqual(expected, Run(connection, $"{prelude} select * from {database}sys.objects where {predicate}"), $"{database} warm: {predicate}");
        }
    }

    [TestMethod]
    public void TypeFilter_OnAColdMsdb_ListsItsShippedObjects()
    {
        var expected = Run(new Simulation().CreateDbConnection().Opened(), "select count(*) from (select * from msdb.sys.objects) o where type = 'U'");
        var cold = Run(new Simulation().CreateDbConnection().Opened(), "select count(*) from msdb.sys.objects where type = 'U'");
        AreEqual(expected, cold);
        IsGreaterThan(0, int.Parse(cold, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow("o.type = 'U'")]
    [DataRow("o.type = 'S'")]
    [DataRow("o.type in ('U', 'PK')")]
    [DataRow("o.type = 'U' and t.is_ms_shipped = 0")]
    public void JoinFromTables_ReturnsWhatTheUnfilteredViewDoes(string predicate)
    {
        var connection = Prepare(new Simulation());
        string Query(string source) => $"select o.object_id, o.name, o.type, t.name from sys.tables t join {source} o on o.object_id = t.object_id where {predicate}";
        var expected = Run(connection, Query("(select * from sys.objects)"));
        AreEqual(expected, Run(connection, Query("sys.objects")));
        _ = Run(connection, "select count(*) from sys.objects");
        AreEqual(expected, Run(connection, Query("sys.objects")));
    }

    [TestMethod]
    [DataRow("sys.all_objects", "type = 'S'")]
    [DataRow("sys.all_objects", "type = 'U'")]
    [DataRow("sys.tables", "type = 'U'")]
    [DataRow("sysobjects", "xtype = 'S'")]
    [DataRow("sysobjects", "xtype = 'U'")]
    public void ViewsOverObjects_AreUnaffected(string view, string predicate)
    {
        var connection = Prepare(new Simulation());
        var expected = Run(connection, $"select * from (select * from {view}) o where {predicate}");
        AreEqual(expected, Run(connection, $"select * from {view} where {predicate}"));
    }

    [TestMethod]
    [DataRow("dbo.t1", "U")]
    [DataRow("t2", "U")]
    [DataRow("q", "SQ")]
    [DataRow("sys.objects", "V")]
    [DataRow("sys.sysschobjs", "S")]
    [DataRow("dbo.t1", "V")]
    public void ObjectId_FindsTheObjectTheViewLists(string name, string type)
    {
        var connection = Prepare(new Simulation());
        _ = Run(connection, "select count(*) from sys.objects where type = 'U'");
        var parts = name.Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var expected = Run(connection, $"select min(object_id) from (select * from sys.all_objects) o where o.type = '{type}' and o.name = '{parts[^1]}' and o.schema_id = schema_id('{schema}')");
        AreEqual(expected, Run(connection, $"select object_id('{name}', '{type}')"));
    }

    [TestMethod]
    [DataRow("type = 'U'")]
    [DataRow("type = 'S'")]
    [DataRow("type = 'PK'")]
    [DataRow("type = 'u'")]
    public void RestrictedPrincipal_SeesWhatItSawFiltered(string predicate)
    {
        var connection = Prepare(new Simulation());
        Execute(connection, "create user u without login; grant select on object::dbo.t1 to u; grant view definition on object::dbo.t2 to u");
        Execute(connection, "execute as user = 'u'");
        var expected = Run(connection, $"select * from (select * from sys.objects) o where {predicate}");
        AreEqual(expected, Run(connection, $"select * from sys.objects where {predicate}"));
        if (predicate == "type = 'U'")
            IsTrue(expected.Contains("t1", StringComparison.Ordinal) && expected.Contains("t2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TypeEqualsInteger_MeetsTheSystemRows_AndRaisesMsg245()
    {
        var rows = Run(new Simulation().CreateDbConnection().Opened(), "select * from sys.objects where type = 1");
        StartsWith("error 245:", rows);
    }
}

internal static class OpenedExtensions
{
    public static DbConnection Opened(this DbConnection connection)
    {
        connection.Open();
        return connection;
    }
}
