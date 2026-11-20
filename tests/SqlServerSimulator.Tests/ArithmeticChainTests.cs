using System.Collections.Concurrent;
using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A flat chain of operators (<c>a + b + c + …</c>) keeps the walk of its left
/// spine on the head node, and <c>int + int</c> adds without the general
/// dispatcher. Neither may change an answer: the evaluation order, the literal
/// sizing, the overflow's number and text, the absorbing session options and
/// the result type are those of the general path, and a cached plan's chain is
/// shared by every execution of it.
/// </summary>
[TestClass]
public sealed class ArithmeticChainTests
{
    private static Simulation WithRows(string values)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (id int primary key, a int, b int); insert t values {values}");
        return simulation;
    }

    [TestMethod]
    public void LongChain_EvaluatesLeftToRight()
    {
        var simulation = WithRows("(1, 1, 10), (2, 2, 20), (3, 3, 30)");
        var terms = string.Join(" + ", Enumerable.Range(1, 1000));

        // 1 + 2 + … + 1000 is 500500, over each row's a.
        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(
            new object?[] { 500501, 500502, 500503 },
            Extensions.FirstColumn(connection, $"select a + {terms} from t order by id"));
        AreEqual(500500, simulation.ExecuteScalar<int>($"select {terms}"));

        // int, bigint, decimal, then int again: the type climbs at each step and stays there.
        CollectionAssert.AreEqual(
            new object?[] { 5.5m, 15.5m, 25.5m },
            Extensions.FirstColumn(connection, "select b + cast(3 as bigint) + cast(1.5 as decimal(5, 2)) + 0 - 9 + a * 0 from t order by id"));
        AreEqual("decimal", simulation.ExecuteScalar("select cast(sql_variant_property(1 + cast(1 as bigint) + cast(1.5 as decimal(5, 2)) + 1, 'BaseType') as varchar(20))"));

        // A string concatenation chain, and a string meeting numbers: the string takes the number's type.
        CollectionAssert.AreEqual(
            new object?[] { "x1y2", "x2y3", "x3y4" },
            Extensions.FirstColumn(connection, "select 'x' + cast(a as varchar(5)) + 'y' + cast(a + 1 as varchar(5)) from t order by id"));
        CollectionAssert.AreEqual(
            new object?[] { 7, 8, 9 },
            Extensions.FirstColumn(connection, "select a + 1 + '2' + 3 from t order by id"));
        AreEqual(6, simulation.ExecuteScalar<int>("select '1' + 2 + 3"));
        AreEqual(string.Concat(Enumerable.Repeat("ab", 50)), simulation.ExecuteScalar($"select {string.Join(" + ", Enumerable.Repeat("'ab'", 50))}"));
    }

    [TestMethod]
    public void ChainOverflow_RaisesOnTheRowAndOperator()
    {
        // Only the third row's second + leaves int: 1000 + 1000 + 2147483000.
        var simulation = WithRows("(1, 1, 0), (2, 2, 0), (3, 1000, 0)");
        const string Query = "select a + a + 2147483000 from t order by id";

        simulation.AssertSqlError(Query, 8115, "Arithmetic overflow error converting expression to data type int.");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(
            new object?[] { 2147483002, 2147483004 },
            Extensions.FirstColumn(connection, "select a + a + 2147483000 from t where id < 3 order by id"));

        // ARITHABORT OFF with ANSI_WARNINGS OFF answers the overflowed row with NULL and carries on.
        CollectionAssert.AreEqual(
            new object?[] { 2147483002, 2147483004, DBNull.Value },
            Extensions.FirstColumn(connection, "set arithabort off; set ansi_warnings off; " + Query));
    }

    [TestMethod]
    public void CachedChain_ReplayedConcurrently()
    {
        var simulation = WithRows("(1, 1, 10), (2, 2, 20), (3, 3, 30), (4, 4, 40)");
        const int Workers = 8;
        const int Rounds = 200;
        var failures = new ConcurrentQueue<string>();

        _ = Parallel.For(0, Workers, worker =>
        {
            using var connection = simulation.CreateOpenConnection();
            for (var round = 0; round < Rounds; round++)
            {
                var p = (worker * 1000) + round;
                using var command = connection.CreateCommand("select sum(a + b + @p) from t", ("@p", p));
                var actual = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                // (1 + 2 + 3 + 4) + (10 + 20 + 30 + 40) + 4 * p
                if (actual != 110 + (4 * p))
                    failures.Enqueue($"worker {worker} round {round}: {actual}");
            }
        });

        IsEmpty(failures, string.Join("; ", failures.Take(5)));
    }

    [TestMethod]
    [DataRow("cast(2147483647 as int) + cast(1 as int)")]
    [DataRow("cast(-2147483648 as int) + cast(-1 as int)")]
    [DataRow("cast(2147483647 as int) + cast(2147483647 as int)")]
    [DataRow("2147483647 + 1")]
    public void IntPlusInt_Overflow_MatchesTheGeneralPath(string expression) =>
        new Simulation().AssertSqlError(
            $"select {expression}",
            8115,
            "Arithmetic overflow error converting expression to data type int.");

    [TestMethod]
    public void IntPlusInt_InRangeAndPromoted_AnswerTheGeneralPathsValuesAndTypes()
    {
        var simulation = new Simulation();

        // The edges stay int.
        AreEqual(int.MaxValue, simulation.ExecuteScalar<int>("select cast(2147483646 as int) + cast(1 as int)"));
        AreEqual(int.MinValue, simulation.ExecuteScalar<int>("select cast(-2147483647 as int) + cast(-1 as int)"));
        AreEqual(0, simulation.ExecuteScalar<int>("select cast(2147483647 as int) + cast(-2147483647 as int)"));

        // A bigint partner promotes, so the same sum doesn't overflow.
        AreEqual(2147483648L, simulation.ExecuteScalar<long>("select cast(2147483647 as int) + cast(1 as bigint)"));
        AreEqual("bigint", simulation.ExecuteScalar("select cast(sql_variant_property(cast(2147483647 as int) + cast(1 as bigint), 'BaseType') as varchar(20))"));
        AreEqual("int", simulation.ExecuteScalar("select cast(sql_variant_property(cast(1 as int) + cast(1 as int), 'BaseType') as varchar(20))"));

        // A typed NULL on either side is NULL, still an int.
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select cast(null as int) + cast(1 as int)"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select cast(1 as int) + cast(null as int)"));

        // A smaller integer type is not the fast path's pair: tinyint still overflows as tinyint.
        simulation.AssertSqlError(
            "select cast(255 as tinyint) + cast(1 as tinyint)",
            8115,
            "Arithmetic overflow error converting expression to data type tinyint.");
    }

    [TestMethod]
    public void IntPlusInt_OverTableColumns_NullsAndOverflowOnTheRow()
    {
        var simulation = WithRows("(1, 1, 2), (2, null, 2), (3, 2147483647, 1)");
        simulation.AssertSqlError("select a + b from t order by id", 8115, "Arithmetic overflow error converting expression to data type int.");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(
            new object?[] { 3, DBNull.Value },
            Extensions.FirstColumn(connection, "select a + b from t where id < 3 order by id"));
        CollectionAssert.AreEqual(
            new object?[] { 3, DBNull.Value, DBNull.Value },
            Extensions.FirstColumn(connection, "set arithabort off; set ansi_warnings off; select a + b from t order by id"));
    }
}
