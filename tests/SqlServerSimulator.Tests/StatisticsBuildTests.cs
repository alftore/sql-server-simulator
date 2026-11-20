using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Building a statistic over a computed key, and the automatic statistics a
/// predicate over a view or a computed column creates. Every expected value
/// was probed 2026-10-06 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class StatisticsBuildTests
{
    private const string Overflowing = "create table ts (a int, c as a * 1000000000); insert ts values (1), (5);";

    private static string? Names(Simulation simulation, string table) =>
        simulation.ExecuteScalar($"select string_agg(name, ',') within group (order by name) from sys.stats where object_id = object_id('{table}')") as string;

    [TestMethod]
    public void AKeyThatFailsToEvaluate_LeavesTheStatisticUnbuilt()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Overflowing);
        _ = simulation.AssertSqlError("create statistics s on ts(c)", 8115);
        _ = simulation.AssertSqlError("create statistics s2 on ts(a, c)", 8115);
        _ = simulation.AssertSqlError("create statistics s3 on ts(c) with fullscan", 8115);
        AreEqual("s,s2,s3", Names(simulation, "ts"));
        AreEqual(1, simulation.ExecuteScalar("select case when stats_date(object_id, stats_id) is null then 1 else 0 end from sys.stats where object_id = object_id('ts') and name = 's'"));
        _ = simulation.AssertSqlError("update statistics ts s", 8115);
    }

    [TestMethod]
    public void AFilter_LimitsTheRowsTheKeyEvaluatesOver()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Overflowing + " create statistics s4 on ts(c) where a < 2");
        AreEqual("s4", Names(simulation, "ts"));
    }

    [TestMethod]
    public void TheFailure_EndsOnlyItsStatement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Overflowing);
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; create statistics s5 on ts(c); create table after (a int)").ExecuteNonQuery());
        AreEqual(1, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(1, connection.CreateCommand("select count(*) from sys.tables where name = 'after'").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        IsNull(Names(simulation, "ts"));
    }

    [TestMethod]
    public void APredicateOnAComputedColumn_LoadsTheColumnsItReads()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t3 (a int, b int, c as a * 2); insert t3 values (5, 1)");
        _ = simulation.ExecuteScalar("select * from t3 where c = 1");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.stats s join sys.stats_columns sc on sc.object_id = s.object_id and sc.stats_id = s.stats_id where s.object_id = object_id('t3') and s.auto_created = 1 and sc.column_id = 1"));
    }

    [TestMethod]
    public void APredicateOnAViewsColumn_LoadsItsBaseColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int); insert t values (1, 2, 3), (3, 4, 5)");
        _ = simulation.ExecuteNonQuery("create view v as select a, b from t");
        _ = simulation.ExecuteScalar("select * from v where b = 2");
        AreEqual("b", simulation.ExecuteScalar("select c.name from sys.stats s join sys.stats_columns sc on sc.object_id = s.object_id and sc.stats_id = s.stats_id join sys.columns c on c.object_id = sc.object_id and c.column_id = sc.column_id where s.object_id = object_id('t')"));
    }

    private static string Dump(Simulation simulation, string statement)
    {
        using var reader = simulation.ExecuteReader(statement);
        var text = new System.Text.StringBuilder();
        do
        {
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    _ = text.Append(i == 0 ? "" : "|").Append(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                _ = text.Append('\n');
            }
            _ = text.Append("--\n");
        }
        while (reader.NextResult());
        return text.ToString();
    }

    [TestMethod]
    public void TheBuiltSnapshot_OverNullsMultiColumnComputedAndFilteredStatistics_IsPinned()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(
            """
            create table m (id int not null constraint pk_m primary key, a nvarchar(20) null, b int null, c as b * 2, d nvarchar(max) null);
            insert m (id, a, b, d) values
                (1, N'x', 1, N'one'), (2, N'X', 1, null), (3, N'y', 2, N'two'), (4, null, 2, N'three'),
                (5, null, null, null), (6, N'z', null, N'six'), (7, N'x', 3, N'seven'), (8, N'y', 3, null),
                (9, null, 3, N'nine'), (10, N'ab', 4, N'ten');
            create statistics s_a on m(a);
            create statistics s_ab on m(a, b);
            create statistics s_ba on m(b, a);
            create statistics s_c on m(c);
            create statistics s_cb on m(c, a);
            create statistics s_f on m(a) where b > 1;
            create statistics s_fb on m(b, a) where b > 1;
            create nonclustered index ix_a on m(a);
            """);

        var dump = new System.Text.StringBuilder();
        foreach (var name in new[] { "s_a", "s_ab", "s_ba", "s_c", "s_cb", "s_f", "s_fb", "ix_a", "pk_m" })
        {
            _ = dump.Append(name).Append('\n')
                .Append(Dump(simulation, $"dbcc show_statistics(N'm', N'{name}') with density_vector"))
                .Append(Dump(simulation, $"dbcc show_statistics(N'm', N'{name}') with histogram"));
        }
        _ = dump.Append(Dump(simulation, "select s.name, p.rows, p.rows_sampled, p.unfiltered_rows from sys.stats s cross apply sys.dm_db_stats_properties(s.object_id, s.stats_id) p where s.object_id = object_id('m') order by s.name"));

        // Captured by running this test against the code before the cost change.
        AreEqual(
            """
            s_a
            0.2|1.6|a
            --
            |0|3|0|1
            ab|0|1|0|1
            x|0|3|0|1
            y|0|2|0|1
            z|0|1|0|1
            --
            s_ab
            0.2|1.6|a
            0.11111111|4.8|a, b
            --
            |0|3|0|1
            ab|0|1|0|1
            x|0|3|0|1
            y|0|2|0|1
            z|0|1|0|1
            --
            s_ba
            0.2|3.2|b
            0.11111111|4.8|b, a
            --
            |0|2|0|1
            1|0|2|0|1
            2|0|2|0|1
            3|0|3|0|1
            4|0|1|0|1
            --
            s_c
            0.2|3.2|c
            --
            |0|2|0|1
            2|0|2|0|1
            4|0|2|0|1
            6|0|3|0|1
            8|0|1|0|1
            --
            s_cb
            0.2|3.2|c
            0.11111111|4.8|c, a
            --
            |0|2|0|1
            2|0|2|0|1
            4|0|2|0|1
            6|0|3|0|1
            8|0|1|0|1
            --
            s_f
            0.25|1.6666666|a
            --
            |0|2|0|1
            ab|0|1|0|1
            x|0|1|0|1
            y|0|2|0|1
            --
            s_fb
            0.33333334|4|b
            0.16666667|5.6666665|b, a
            --
            2|0|2|0|1
            3|0|3|0|1
            4|0|1|0|1
            --
            ix_a
            0.2|1.6|a
            0.1|5.6|a, id
            --
            |0|3|0|1
            ab|0|1|0|1
            x|0|3|0|1
            y|0|2|0|1
            z|0|1|0|1
            --
            pk_m
            --
            --
            ix_a|10|10|10
            pk_m|||
            s_a|10|10|10
            s_ab|10|10|10
            s_ba|10|10|10
            s_c|10|10|10
            s_cb|10|10|10
            s_f|6|6|10
            s_fb|6|6|10
            --

            """.ReplaceLineEndings("\n"), dump.ToString());
    }
}
