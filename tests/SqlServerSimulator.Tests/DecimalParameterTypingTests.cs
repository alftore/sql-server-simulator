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
    public void UnsetPrecision_NewValueEachExecution_DeclaresEachValuesType()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select sql_variant_property(cast(@d as sql_variant), 'Precision'), sql_variant_property(cast(@d as sql_variant), 'Scale')");
        var parameter = new SimulatedDbParameter { ParameterName = "@d", DbType = DbType.Decimal };
        _ = command.Parameters.Add(parameter);

        (object Value, int? Precision, int? Scale)[] runs = [(1.5m, 2, 1), (123.456m, 6, 3), (DBNull.Value, null, null), (1.5m, 2, 1)];
        foreach (var (value, precision, scale) in runs)
        {
            parameter.Value = value;
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            AreEqual((object?)precision ?? DBNull.Value, reader.GetValue(0));
            AreEqual((object?)scale ?? DBNull.Value, reader.GetValue(1));
        }
    }

    [TestMethod]
    public void SameValue_PrecisionChangedBetweenExecutions_TakesTheNewType()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var parameter = new SimulatedDbParameter { ParameterName = "@d", DbType = DbType.Decimal, Value = 1.239m };
        using var command = connection.CreateCommand("select sql_variant_property(cast(@d as sql_variant), 'Precision'), sql_variant_property(cast(@d as sql_variant), 'Scale'), convert(nvarchar(50), @d)");
        _ = command.Parameters.Add(parameter);

        (byte Precision, byte Scale, int ExpectedPrecision, int ExpectedScale, string Text)[] runs =
            [(0, 0, 4, 3, "1.239"), (18, 2, 18, 2, "1.24"), (0, 0, 4, 3, "1.239"), (10, 0, 10, 3, "1.239"), (9, 5, 9, 5, "1.23900")];
        foreach (var (precision, scale, expectedPrecision, expectedScale, text) in runs)
        {
            parameter.Precision = precision;
            parameter.Scale = scale;
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            AreEqual(expectedPrecision, Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            AreEqual(expectedScale, Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
            AreEqual(text, reader.GetString(2));
        }
    }

    [TestMethod]
    public void OutOfRange_AfterAGoodValue_StillRefusedBeforeSending()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var parameter = new SimulatedDbParameter { ParameterName = "@d", DbType = DbType.Decimal, Precision = 4, Scale = 2, Value = 12.34m };
        using var command = connection.CreateCommand("select @d");
        _ = command.Parameters.Add(parameter);
        AreEqual(12.34m, command.ExecuteScalar());

        parameter.Value = 123.45m;
        AreEqual("Parameter value '123.45' is out of range.", Throws<ArgumentException>(() => _ = command.ExecuteScalar()).Message);
        parameter.Value = 1.5m;
        AreEqual(1.50m, command.ExecuteScalar());
        parameter.Precision = 2;
        AreEqual("Parameter value '1.50' is out of range.", Throws<ArgumentException>(() => _ = command.ExecuteScalar()).Message);
    }

    [TestMethod]
    public void UnconvertibleValue_RaisesAsBefore()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var parameter = new SimulatedDbParameter { ParameterName = "@d", DbType = DbType.Decimal, Value = "not a number" };
        using var command = connection.CreateCommand("select @d");
        _ = command.Parameters.Add(parameter);

        var first = Throws<Exception>(() => _ = command.ExecuteScalar());
        var second = Throws<Exception>(() => _ = command.ExecuteScalar());
        AreEqual(first.GetType(), second.GetType());
        AreEqual(first.Message, second.Message);

        parameter.Value = 2.5m;
        AreEqual(2.5m, command.ExecuteScalar());
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

    private static object TemporalValue(DbType dbType, string text) => dbType switch
    {
        DbType.DateTime2 => DateTime.Parse(text, CultureInfo.InvariantCulture),
        DbType.Time => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
        _ => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
    };

    [TestMethod]
    [DataRow(DbType.DateTime2, 3, "2026-01-02T03:04:05.1234567", "2026-01-02 03:04:05.123")]
    [DataRow(DbType.DateTime2, 3, "2026-01-02T03:04:05.9999999", "2026-01-02 03:04:05.999")]
    [DataRow(DbType.DateTime2, 0, "2026-01-02T03:04:05.1234567", "2026-01-02 03:04:05.1234567")]
    [DataRow(DbType.DateTime2, 7, "2026-01-02T03:04:05.1234567", "2026-01-02 03:04:05.1234567")]
    [DataRow(DbType.Time, 3, "03:04:05.1234567", "03:04:05.123")]
    [DataRow(DbType.Time, 0, "03:04:05.1234567", "03:04:05.1234567")]
    [DataRow(DbType.DateTimeOffset, 3, "2026-01-02T03:04:05.9999999+02:00", "2026-01-02 03:04:05.999 +02:00")]
    [DataRow(DbType.DateTimeOffset, 0, "2026-01-02T03:04:05.1234567+02:00", "2026-01-02 03:04:05.1234567 +02:00")]
    public void TemporalParameter_TruncatesToItsScale(DbType dbType, int scale, string value, string expected)
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select convert(nvarchar(60), @t, 121)";
        _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@t", DbType = dbType, Scale = (byte)scale, Value = TemporalValue(dbType, value) });

        AreEqual(expected, command.ExecuteScalar());
    }

    [TestMethod]
    [DataRow(DbType.DateTime2, 3, "(@t datetime2(3),@x int)")]
    [DataRow(DbType.DateTime2, 0, "(@t datetime2(7),@x int)")]
    [DataRow(DbType.Time, 3, "(@t time(3),@x int)")]
    [DataRow(DbType.DateTimeOffset, 3, "(@t datetimeoffset(3),@x int)")]
    public void TemporalParameter_IsDeclaredAtItsScale(DbType dbType, int scale, string declaration)
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select @t, @x";
        _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@t", DbType = dbType, Scale = (byte)scale, Value = DBNull.Value });
        _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@x", DbType = DbType.Int32, Direction = ParameterDirection.Input, Value = null });

        AreEqual(
            $"The parameterized query '{declaration}select @t, @x' expects the parameter '@x', which was not supplied.",
            Throws<SimulatedSqlException>(() => _ = command.ExecuteScalar()).Message);
    }
}
