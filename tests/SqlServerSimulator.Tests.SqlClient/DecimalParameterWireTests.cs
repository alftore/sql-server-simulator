using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The wire twin of <c>DecimalParameterTypingTests</c>: for each case, the
/// in-process result for a <see cref="SimulatedDbParameter"/> equals the
/// result through real SqlClient 7.1.1 against the listener, in the type the
/// variable has, the value it holds, the declaration Msg 8178 quotes and the
/// client-side <see cref="ArgumentException"/> for a value that does not fit.
/// The suite's <c>fi-FI</c> culture pins the message's decimal point.
/// </summary>
[TestClass]
public sealed class DecimalParameterWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string TypeQuery = "select cast(sql_variant_property(cast(@d as sql_variant), 'Precision') as int) * 100 + cast(sql_variant_property(cast(@d as sql_variant), 'Scale') as int), convert(nvarchar(50), @d)";

    private const string DeclarationQuery = "select @d, @x";

    [TestMethod]
    [DataRow(18, 2, "1.5")]
    [DataRow(18, 2, "1.239")]
    [DataRow(18, 2, "1.235")]
    [DataRow(18, 2, "-1.235")]
    [DataRow(18, 2, null)]
    [DataRow(4, 2, "123.45")]
    [DataRow(0, 0, "1.5")]
    [DataRow(0, 0, "5")]
    [DataRow(0, 0, "0.001")]
    [DataRow(0, 0, "50000.00")]
    [DataRow(0, 0, null)]
    [DataRow(18, 0, "1.5")]
    [DataRow(0, 2, "5")]
    [DataRow(0, 2, "123.456")]
    [DataRow(10, 0, "1.5")]
    [DataRow(38, 10, "1.5")]
    public async Task InProcess_MatchesSqlClient(int precision, int scale, string? text)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        object value = text is null ? DBNull.Value : decimal.Parse(text, CultureInfo.InvariantCulture);

        var wireType = await Run(WireCommand(connection, TypeQuery, precision, scale, value, withUnsupplied: false));
        var inProcessType = await Run(InProcessCommand(simulation, TypeQuery, precision, scale, value, withUnsupplied: false));
        AreEqual(wireType, inProcessType);

        var wireDeclaration = await Run(WireCommand(connection, DeclarationQuery, precision, scale, value, withUnsupplied: true));
        var inProcessDeclaration = await Run(InProcessCommand(simulation, DeclarationQuery, precision, scale, value, withUnsupplied: true));
        AreEqual(wireDeclaration, inProcessDeclaration);
    }

    /// <summary>The first row of <paramref name="command"/> as text, or the type and message of what it raised.</summary>
    private async Task<string> Run(System.Data.Common.DbCommand command)
    {
        await using (command)
        {
            try
            {
                await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
                var row = new List<string>();
                if (await reader.ReadAsync(TestContext.CancellationToken))
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                        row.Add(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
                }
                return string.Join('|', row);
            }
            catch (ArgumentException e)
            {
                return "ArgumentException: " + e.Message;
            }
            catch (System.Data.Common.DbException e)
            {
                return $"Msg {(e as SqlException)?.Number ?? (e as SimulatedSqlException)?.Number}: {e.Message}";
            }
        }
    }

    private static SqlCommand WireCommand(SqlConnection connection, string text, int precision, int scale, object value, bool withUnsupplied)
    {
        var command = new SqlCommand(text, connection);
        _ = command.Parameters.Add(new SqlParameter("@d", SqlDbType.Decimal) { Precision = (byte)precision, Scale = (byte)scale, Value = value });
        if (withUnsupplied)
            _ = command.Parameters.Add(new SqlParameter("@x", SqlDbType.Int) { Value = null });
        return command;
    }

    private static System.Data.Common.DbCommand InProcessCommand(Simulation simulation, string text, int precision, int scale, object value, bool withUnsupplied)
    {
        var command = simulation.CreateDbConnection().CreateCommand();
        command.Connection!.Open();
        command.CommandText = text;
        _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@d", DbType = DbType.Decimal, Precision = (byte)precision, Scale = (byte)scale, Value = value });
        if (withUnsupplied)
            _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@x", DbType = DbType.Int32, Direction = ParameterDirection.Input, Value = null });
        return command;
    }
}
