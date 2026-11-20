using System.Data.Common;
using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A statement's column references bind once per enumeration, by the reference
/// node, rather than by name per row. These pin that the two ways of reading a
/// column agree: every expected value below is what the by-name path returned
/// for the same statement before the binding existed (taken from that build and
/// checked by reading), not a result SQL Server was asked for, so a failure
/// here is the binding disagreeing with the name lookup it replaced. The shapes
/// are the ones a binding could get wrong: one column read through several
/// reference nodes, more references than a frame's table holds, one node read
/// against different sources, a tuple whose rows repeat while the outer values
/// do not, and name errors that must surface at the same row.
/// </summary>
[TestClass]
public sealed class ColumnBindingTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int not null primary key, s nvarchar(20) null, g int not null)",
            "insert t values (1, N'a', 1), (2, NULL, 2), (3, N'ccc', 1)");
        return sim;
    }

    /// <summary>Every row as its values joined by <c>|</c>, rows joined by <c>;</c>, NULL spelled out.</summary>
    private static string Rows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        return Render(reader);
    }

    private static string Render(DbDataReader reader)
    {
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(string.Join('|', values));
        }
        return string.Join(';', rows);
    }

    private static int ErrorNumber(Simulation sim, string sql) =>
        ThrowsExactly<SimulatedSqlException>(() => _ = Rows(sim, sql)).Number;

    /// <summary>
    /// One column through six reference nodes spelled differently: each is its
    /// own binding in the frame and each reads the same value.
    /// </summary>
    [TestMethod]
    public void OneColumn_ThroughSixSpellings_ReadsTheSameValue() =>
        AreEqual(
            "a|a|a|a|a|a;NULL|NULL|NULL|NULL|NULL|NULL;ccc|ccc|ccc|ccc|ccc|ccc",
            Rows(Seeded(), "select s, t.s, dbo.t.s, [s], [t].[s], T.S from t order by id"));

    /// <summary>
    /// 300 distinct references over three rows: the frame's table grows to its
    /// cap and the references that do not fit read by name, with the same
    /// values.
    /// </summary>
    [TestMethod]
    public void ThreeHundredReferences_GrowPastTheTableAndStayCorrect()
    {
        var list = string.Join(", ", Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? $"id + {i}" : $"g * 1000 + {i}"));
        var expected = string.Join(';', new[] { (1, 1), (2, 2), (3, 1) }.Select(row =>
            string.Join('|', Enumerable.Range(0, 300).Select(i => (i % 2 == 0 ? row.Item1 + i : (row.Item2 * 1000) + i).ToString(CultureInfo.InvariantCulture)))));
        AreEqual(expected, Rows(Seeded(), $"select {list} from t order by id"));
    }

    /// <summary>
    /// A self-join reads the same column of the same table through two sources:
    /// the qualified references bind to different slots of the tuple.
    /// </summary>
    [TestMethod]
    public void SelfJoin_BindsEachQualifierToItsOwnSource() =>
        AreEqual(
            "a|a|aa;a|ccc|accc;ccc|a|ccca;ccc|ccc|cccccc",
            Rows(Seeded(), "select a.s, b.s, a.s + b.s from t a join t b on a.g = b.g where a.s is not null and b.s is not null order by a.id, b.id"));

    /// <summary>
    /// A scalar subquery over a one-row table reads an outer column that
    /// differs per outer row: the inner row's array repeats for every outer row
    /// and the outer value must not.
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_OverOneRowTable_ReadsTheOuterRowEachTime()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create table one (x int not null, name nvarchar(5) not null)",
            "insert one values (7, N'n')");
        AreEqual(
            "1|108|na;2|209|NULL;3|110|nccc",
            Rows(sim, "select t.id, (select one.x + t.g * 100 + t.id from one), (select one.name + t.s from one) from t order by t.id"));
    }

    /// <summary>
    /// One cached text run twice with different parameter values and an
    /// <c>INSERT</c> between the runs: the second run is a new enumeration over
    /// new rows, with the same plan.
    /// </summary>
    [TestMethod]
    public void CachedText_RunTwiceWithAnInsertBetween_ReadsEachRunsRows()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        AreEqual("1|a", Run(connection, 1));
        AreEqual("3|ccc", Run(connection, 3));
        _ = connection.CreateCommand("insert t values (4, N'dddd', 4), (5, N'e', 1)").ExecuteNonQuery();
        AreEqual("4|dddd", Run(connection, 4));
        AreEqual("1|a;3|ccc;5|e", Run(connection, 1, byGroup: true));
        AreEqual("2|NULL", Run(connection, 2));

        static string Run(DbConnection connection, int value, bool byGroup = false)
        {
            using var command = connection.CreateCommand();
            command.CommandText = byGroup
                ? "select id, s from t where g = @p order by id"
                : "select id, s from t where id = @p order by id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p";
            parameter.Value = value;
            _ = command.Parameters.Add(parameter);
            using var reader = command.ExecuteReader();
            return Render(reader);
        }
    }

    /// <summary>
    /// A nested query's <c>ORDER BY</c> without <c>TOP</c> does not bind at
    /// parse, so an unknown name raises at the first row that reaches it and
    /// never over an empty source; an ambiguous one is Msg 209, again on a second
    /// execution (a name error is never remembered).
    /// </summary>
    [TestMethod]
    public void NestedOrderByNameErrors_RaiseAtTheFirstRowAndOnEveryRun()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create table u (x int not null)");
        const string UnknownTop = "select (select top 1 x from u order by nosuch) from t";
        const string UnknownXml = "select (select x from u order by nosuch for xml path('')) from t";
        const string Ambiguous = "select (select a.x + 0 from u a, u b order by x for xml path('')) from t";

        // TOP makes the ORDER BY bind at parse, empty source or not; without it
        // nothing binds until a row reaches it, so over an empty u no row does.
        AreEqual(207, ErrorNumber(sim, UnknownTop));
        AreEqual("NULL;NULL;NULL", Rows(sim, UnknownXml));
        AreEqual("NULL;NULL;NULL", Rows(sim, Ambiguous));

        sim.ExecuteBatches("insert u values (5), (6)");
        AreEqual(207, ErrorNumber(sim, UnknownTop));
        AreEqual(207, ErrorNumber(sim, UnknownXml));
        AreEqual(209, ErrorNumber(sim, Ambiguous));
        AreEqual(209, ErrorNumber(sim, Ambiguous));
    }
}
