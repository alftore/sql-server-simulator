using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>int - int</c>, <c>int * int</c>, <c>int / int</c> and <c>int % int</c>
/// run without the general dispatcher, as <c>int + int</c> does
/// (<see cref="ArithmeticChainTests"/>). None may change an answer: the value,
/// the <c>int</c> result type, Msg 8115 naming <c>int</c> (<c>int.MinValue / -1</c>
/// and <c>int.MinValue % -1</c> included, as real forms the quotient for both),
/// Msg 8134, truncation toward zero, the remainder's sign, NULLs, and the
/// absorption under <c>ARITHABORT OFF</c> with <c>ANSI_WARNINGS OFF</c>.
/// </summary>
[TestClass]
public sealed class IntFastPathTests
{
    private const string Overflow = "Arithmetic overflow error converting expression to data type int.";
    private const string DivideByZero = "Divide by zero error encountered.";

    private static readonly int[] Edges = [0, 1, -1, 7, -7, 3, int.MaxValue, int.MinValue, int.MaxValue - 1, int.MinValue + 1];

    /// <summary>The answer the operator gives, or the error number it raises.</summary>
    private static (int? Value, int Error) Oracle(int a, char op, int b)
    {
        if (op is '/' or '%' && b == 0)
            return (null, 8134);
        if (op is '/' or '%' && a == int.MinValue && b == -1)
            return (null, 8115);
        var result = op switch
        {
            '-' => (long)a - b,
            '*' => (long)a * b,
            '/' => a / b,
            _ => a % b,
        };
        return result is >= int.MinValue and <= int.MaxValue ? ((int)result, 0) : (null, 8115);
    }

    [TestMethod]
    public void IntOperators_MatchTheGeneralPath()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, a int, b int)");
        var id = 0;
        foreach (var a in Edges)
        {
            foreach (var b in Edges)
                _ = simulation.ExecuteNonQuery(string.Create(CultureInfo.InvariantCulture, $"insert t values ({++id}, {a}, {b})"));
        }

        foreach (var op in "-*/%")
        {
            id = 0;
            foreach (var a in Edges)
            {
                foreach (var b in Edges)
                {
                    id++;
                    var (expected, error) = Oracle(a, op, b);
                    var what = $"{a} {op} {b}";
                    var literal = string.Create(CultureInfo.InvariantCulture, $"cast({a} as int) {op} cast({b} as int)");
                    var column = $"a {op} b from t where id = {id}";
                    if (error == 0)
                    {
                        AreEqual(expected, simulation.ExecuteScalar<int>($"select {literal}"), what);
                        AreEqual(expected, simulation.ExecuteScalar<int>($"select {column}"), what);
                        AreEqual("int", simulation.ExecuteScalar($"select cast(sql_variant_property({literal}, 'BaseType') as varchar(20))"), what);
                        // The same pair through bigint, narrowed back, is the general path's own answer.
                        AreEqual(expected, simulation.ExecuteScalar<int>($"select cast(cast(a as bigint) {op} b as int) from t where id = {id}"), what);
                    }
                    else
                    {
                        _ = simulation.AssertSqlError($"select {literal}", error);
                        _ = simulation.AssertSqlError($"select {column}", error);
                    }
                }
            }
        }
    }

    [TestMethod]
    [DataRow("cast(-2147483648 as int) - cast(1 as int)")]
    [DataRow("cast(-2147483647 as int) - cast(2 as int)")]
    [DataRow("cast(0 as int) - cast(-2147483648 as int)")]
    [DataRow("cast(2147483647 as int) - cast(-1 as int)")]
    [DataRow("cast(2147483647 as int) * cast(2 as int)")]
    [DataRow("cast(-2147483648 as int) * cast(-1 as int)")]
    [DataRow("cast(-1 as int) * cast(-2147483648 as int)")]
    [DataRow("cast(65536 as int) * cast(32768 as int)")]
    [DataRow("cast(-65536 as int) * cast(32769 as int)")]
    [DataRow("cast(-2147483648 as int) / cast(-1 as int)")]
    [DataRow("cast(-2147483648 as int) % cast(-1 as int)")]
    [DataRow("2147483647 * 2")]
    public void IntOverflow_RaisesMsg8115NamingInt(string expression) =>
        new Simulation().AssertSqlError($"select {expression}", 8115, Overflow);

    [TestMethod]
    [DataRow("cast(-5 as int) % cast(-1 as int)", 0)]
    [DataRow("cast(-2147483648 as int) % cast(1 as int)", 0)]
    [DataRow("cast(-2147483648 as int) / cast(1 as int)", int.MinValue)]
    [DataRow("cast(2147483647 as int) / cast(-1 as int)", -int.MaxValue)]
    [DataRow("cast(-2147483647 as int) / cast(-1 as int)", int.MaxValue)]
    [DataRow("cast(-2147483648 as int) * cast(1 as int)", int.MinValue)]
    [DataRow("cast(-2147483647 as int) - cast(1 as int)", int.MinValue)]
    [DataRow("cast(2147483647 as int) - cast(2147483647 as int)", 0)]
    [DataRow("cast(46340 as int) * cast(46340 as int)", 2147395600)]
    [DataRow("cast(-1 as int) * cast(2147483647 as int)", -int.MaxValue)]
    public void IntEdges_StayInRange(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    [TestMethod]
    [DataRow("/")]
    [DataRow("%")]
    public void IntDivideByZero_RaisesMsg8134_OrNullWhenAbsorbed(string op)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, a int, b int); insert t values (1, 10, 0), (2, 10, 5), (3, -2147483648, 0), (4, null, 0)");
        simulation.AssertSqlError($"select cast(5 as int) {op} cast(0 as int)", 8134, DivideByZero);
        simulation.AssertSqlError($"select cast(-2147483648 as int) {op} cast(0 as int)", 8134, DivideByZero);
        simulation.AssertSqlError($"select a {op} b from t where id = 1", 8134, DivideByZero);
        simulation.AssertSqlError($"select a {op} b from t where id = 3", 8134, DivideByZero);
        // The first row's divisor is the zero; the second row is never reached.
        simulation.AssertSqlError($"select a {op} b from t where id < 3 order by id", 8134, DivideByZero);

        // Absorbed, every zero divisor answers NULL (a NULL dividend included), and the rows beside it keep their answers.
        using var connection = simulation.CreateOpenConnection();
        var expected = op == "/" ? 2 : 0;
        CollectionAssert.AreEqual(
            new object?[] { DBNull.Value, expected, DBNull.Value, DBNull.Value },
            Extensions.FirstColumn(connection, $"set arithabort off; set ansi_warnings off; select a {op} b from t order by id"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar($"set arithabort off; set ansi_warnings off; select cast(5 as int) {op} cast(0 as int)"));
    }

    [TestMethod]
    public void IntOverflow_IsAbsorbedToNull_UnderArithabortOffAnsiWarningsOff()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, a int, b int); insert t values (1, -2147483648, 1), (2, -2147483648, -1), (3, 2147483647, 2), (4, 3, 4)");
        using var connection = simulation.CreateOpenConnection();
        const string Absorb = "set arithabort off; set ansi_warnings off; ";

        // - : -2^31 - 1 overflows; -2^31 - -1, 2^31-1 - 2 and 3 - 4 don't.
        CollectionAssert.AreEqual(new object?[] { DBNull.Value, -2147483647, 2147483645, -1 }, Extensions.FirstColumn(connection, Absorb + "select a - b from t order by id"));
        // * : the middle two leave int.
        CollectionAssert.AreEqual(new object?[] { int.MinValue, DBNull.Value, DBNull.Value, 12 }, Extensions.FirstColumn(connection, Absorb + "select a * b from t order by id"));
        // / and % : int.MinValue / -1 is the one overflow, for both.
        CollectionAssert.AreEqual(new object?[] { int.MinValue, DBNull.Value, 1073741823, 0 }, Extensions.FirstColumn(connection, Absorb + "select a / b from t order by id"));
        CollectionAssert.AreEqual(new object?[] { 0, DBNull.Value, 1, 3 }, Extensions.FirstColumn(connection, Absorb + "select a % b from t order by id"));

        // Not absorbed by default: the error is real's, on the first row that overflows.
        simulation.AssertSqlError("select a * b from t order by id", 8115, Overflow);
    }

    [TestMethod]
    [DataRow("cast(-7 as int) / cast(3 as int)", -2)]
    [DataRow("cast(7 as int) / cast(-3 as int)", -2)]
    [DataRow("cast(-7 as int) / cast(-3 as int)", 2)]
    [DataRow("cast(7 as int) / cast(3 as int)", 2)]
    [DataRow("cast(1 as int) / cast(2 as int)", 0)]
    [DataRow("cast(-1 as int) / cast(2 as int)", 0)]
    [DataRow("cast(-2147483648 as int) / cast(2 as int)", -1073741824)]
    public void IntQuotient_TruncatesTowardZero(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    [TestMethod]
    [DataRow("cast(-7 as int) % cast(3 as int)", -1)]
    [DataRow("cast(7 as int) % cast(-3 as int)", 1)]
    [DataRow("cast(-7 as int) % cast(-3 as int)", -1)]
    [DataRow("cast(7 as int) % cast(3 as int)", 1)]
    [DataRow("cast(6 as int) % cast(-3 as int)", 0)]
    [DataRow("cast(-6 as int) % cast(3 as int)", 0)]
    [DataRow("cast(-2147483648 as int) % cast(7 as int)", -2)]
    [DataRow("cast(2147483647 as int) % cast(-2147483648 as int)", 2147483647)]
    [DataRow("cast(-2147483648 as int) % cast(2147483647 as int)", -1)]
    public void IntRemainder_TakesTheDividendsSign(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    [TestMethod]
    [DataRow("-", 3)]
    [DataRow("*", 10)]
    [DataRow("/", 2)]
    [DataRow("%", 1)]
    public void IntOperators_NullsAndOtherTypes_TakeTheGeneralPath(string op, int withString)
    {
        var simulation = new Simulation();

        // A typed NULL on either side is NULL; a zero divisor beside NULL is NULL, not Msg 8134.
        foreach (var expression in new[] { $"cast(null as int) {op} cast(1 as int)", $"cast(1 as int) {op} cast(null as int)", $"cast(null as int) {op} cast(null as int)", $"cast(null as int) {op} cast(0 as int)" })
        {
            AreEqual(DBNull.Value, simulation.ExecuteScalar($"select {expression}"), expression);
        }

        // A bigint partner promotes: the pair that overflows int doesn't.
        AreEqual("bigint", simulation.ExecuteScalar($"select cast(sql_variant_property(cast(2147483647 as int) {op} cast(2 as bigint), 'BaseType') as varchar(20))"));
        // A narrower pair keeps its own type.
        AreEqual("smallint", simulation.ExecuteScalar($"select cast(sql_variant_property(cast(5 as smallint) {op} cast(2 as smallint), 'BaseType') as varchar(20))"));
        // A string beside an int converts to the int.
        AreEqual(withString, simulation.ExecuteScalar<int>($"select cast(5 as int) {op} '2'"));
    }

    [TestMethod]
    public void IntOperators_LeaveTheOtherTypesOverflowNamesAlone()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("select cast(-32768 as smallint) - cast(1 as smallint)", 8115, "Arithmetic overflow error converting expression to data type smallint.");
        simulation.AssertSqlError("select cast(200 as tinyint) * cast(2 as tinyint)", 8115, "Arithmetic overflow error converting expression to data type tinyint.");
        simulation.AssertSqlError("select cast(-32768 as smallint) / cast(-1 as smallint)", 8115, "Arithmetic overflow error converting expression to data type smallint.");
        simulation.AssertSqlError("select cast(-9223372036854775808 as bigint) % cast(-1 as bigint)", 8115, "Arithmetic overflow error converting expression to data type bigint.");
        simulation.AssertSqlError("select cast(9223372036854775807 as bigint) * cast(2 as bigint)", 8115, "Arithmetic overflow error converting expression to data type bigint.");
    }

    [TestMethod]
    public void IntOperators_ChainsKeepTheirOrderAndRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, a int, b int); insert t values (1, 100, 7), (2, -100, 7), (3, 2147483647, 2)");
        using var connection = simulation.CreateOpenConnection();

        // ((a - b) * 2) / 3 % 5, left to right: 93 * 2 / 3 % 5 = 2, and -107 * 2 / 3 = -71 % 5 = -1.
        CollectionAssert.AreEqual(
            new object?[] { 2, -1 },
            Extensions.FirstColumn(connection, "select (a - b) * 2 / 3 % 5 from t where id < 3 order by id"));
        // The third row is the first whose * leaves int.
        simulation.AssertSqlError("select (a - b) * 2 / 3 % 5 from t order by id", 8115, Overflow);
        // The division by zero comes before the NULL it is multiplied by.
        simulation.AssertSqlError("select a / (b - b) * cast(null as int) from t", 8134, DivideByZero);
    }
}
