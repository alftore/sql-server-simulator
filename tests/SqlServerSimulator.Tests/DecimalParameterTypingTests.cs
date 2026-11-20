using System.Data;
using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="SimulatedDbParameter.Precision"/> and
/// <see cref="SimulatedDbParameter.Scale"/> as Microsoft.Data.SqlClient 7.1.1
/// honours them (observed 2026-11-20 through the listener, which quotes the
/// declaration SqlClient sent in Msg 8178): the declared type, the rounded
/// value and the client-side refusal of a value that does not fit. The wire
/// twin of every row is in <c>DecimalParameterWireTests</c>.
/// </summary>
[TestClass]
public sealed class DecimalParameterTypingTests
{
    private static SimulatedDbParameter Parameter(byte precision, byte scale, string? value) => new()
    {
        ParameterName = "@d",
        DbType = DbType.Decimal,
        Precision = precision,
        Scale = scale,
        Value = value is null ? DBNull.Value : decimal.Parse(value, CultureInfo.InvariantCulture),
    };

    [TestMethod]
    public void PrecisionAndScale_RoundTrip()
    {
        var parameter = new SimulatedDbParameter();
        AreEqual((byte)0, parameter.Precision);
        AreEqual((byte)0, parameter.Scale);

        parameter.Precision = 18;
        parameter.Scale = 2;
        AreEqual((byte)18, parameter.Precision);
        AreEqual((byte)2, parameter.Scale);

        parameter.DbType = DbType.Decimal;
        parameter.ResetDbType();
        AreEqual((byte)18, parameter.Precision);
        AreEqual((byte)2, parameter.Scale);
    }

    [TestMethod]
    [DataRow(18, 2, "1.5", 1802, "1.50")]
    [DataRow(18, 2, "1.239", 1802, "1.24")]
    [DataRow(18, 2, "1.235", 1802, "1.24")]
    [DataRow(18, 2, "-1.235", 1802, "-1.24")]
    [DataRow(18, 2, null, null, null)]
    [DataRow(0, 0, "1.5", 201, "1.5")]
    [DataRow(0, 0, "5", 100, "5")]
    [DataRow(0, 0, "0.001", 303, "0.001")]
    [DataRow(0, 0, "50000.00", 702, "50000.00")]
    [DataRow(0, 0, null, null, null)]
    [DataRow(18, 0, "1.5", 1801, "1.5")]
    [DataRow(0, 2, "123.456", 602, "123.46")]
    [DataRow(10, 0, "1.5", 1001, "1.5")]
    [DataRow(38, 10, "1.5", 3810, "1.5000000000")]
    public void Parameter_IsTypedAsSqlClientDeclaresIt(int precision, int scale, string? value, int? expectedPrecisionTimes100PlusScale, string? expectedText)
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select cast(sql_variant_property(cast(@d as sql_variant), 'Precision') as int) * 100 + cast(sql_variant_property(cast(@d as sql_variant), 'Scale') as int), convert(nvarchar(50), @d)";
        _ = command.Parameters.Add(Parameter((byte)precision, (byte)scale, value));

        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual((object?)expectedPrecisionTimes100PlusScale ?? DBNull.Value, reader.GetValue(0));
        AreEqual((object?)expectedText ?? DBNull.Value, reader.GetValue(1));
    }

    [TestMethod]
    [DataRow(18, 2, "1.5", "(@d decimal(18,2),@x int)")]
    [DataRow(18, 2, "1.239", "(@d decimal(18,2),@x int)")]
    [DataRow(18, 2, null, "(@d decimal(18,2),@x int)")]
    [DataRow(0, 0, "1.5", "(@d decimal(2,1),@x int)")]
    [DataRow(0, 0, "5", "(@d decimal(1,0),@x int)")]
    [DataRow(0, 0, "0.001", "(@d decimal(3,3),@x int)")]
    [DataRow(0, 0, "50000.00", "(@d decimal(7,2),@x int)")]
    [DataRow(0, 0, null, "(@d decimal(29,0),@x int)")]
    [DataRow(18, 0, "1.5", "(@d decimal(18,1),@x int)")]
    [DataRow(0, 2, "123.456", "(@d decimal(6,2),@x int)")]
    [DataRow(10, 0, "1.5", "(@d decimal(10,1),@x int)")]
    [DataRow(38, 10, "1.5", "(@d decimal(38,10),@x int)")]
    public void Declaration_IsTheOneSqlClientSends(int precision, int scale, string? value, string declaration)
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select @d, @x";
        _ = command.Parameters.Add(Parameter((byte)precision, (byte)scale, value));
        _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@x", DbType = DbType.Int32, Direction = ParameterDirection.Input, Value = null });

        var error = Throws<SimulatedSqlException>(() => _ = command.ExecuteScalar());
        AreEqual(8178, error.Number);
        AreEqual($"The parameterized query '{declaration}select @d, @x' expects the parameter '@x', which was not supplied.", error.Message);
    }

    [TestMethod]
    [DataRow(4, 2, "123.45", "123.45")]
    [DataRow(0, 2, "5", "5.00")]
    public void OutOfRange_RaisesArgumentException_BeforeTheBatchRuns(int precision, int scale, string value, string quoted)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(["create table t (a int)", "create procedure p @d decimal(10, 2) as select @d"]);
        using var connection = sim.CreateOpenConnection();

        using var batch = connection.CreateCommand("insert t values (1); select @d");
        _ = batch.Parameters.Add(Parameter((byte)precision, (byte)scale, value));
        AreEqual($"Parameter value '{quoted}' is out of range.", Throws<ArgumentException>(() => _ = batch.ExecuteScalar()).Message);
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));

        using var procedure = connection.CreateCommand("p");
        procedure.CommandType = CommandType.StoredProcedure;
        _ = procedure.Parameters.Add(Parameter((byte)precision, (byte)scale, value));
        AreEqual($"Parameter value '{quoted}' is out of range.", Throws<ArgumentException>(() => _ = procedure.ExecuteScalar()).Message);
    }

    [TestMethod]
    public void RoundedParameter_MatchesTheRoundedRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (x decimal(18, 2)); insert t values (1.24), (1.25)");
        using var command = sim.CreateOpenConnection().CreateCommand("select count(*) from t where x = @p");
        _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@p", DbType = DbType.Decimal, Precision = 18, Scale = 2, Value = 1.239m });

        AreEqual(1, command.ExecuteScalar());
    }

    [TestMethod]
    public void StoredProcedureArgument_IsRoundedToTheSetScale()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create procedure p @d decimal(10, 3) as select @d");
        using var command = sim.CreateOpenConnection().CreateCommand("p");
        command.CommandType = CommandType.StoredProcedure;
        _ = command.Parameters.Add(Parameter(18, 2, "1.239"));

        AreEqual(1.240m, command.ExecuteScalar());
    }
}
