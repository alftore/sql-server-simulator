using System.Data;
using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The engine runs under the invariant culture whatever the host's, and hands
/// the host's back: after each call, and to the handlers it raises.
/// </summary>
[TestClass]
public sealed class HostCultureTests
{
    [TestMethod]
    public void EngineOutput_IgnoresTheHostCulture()
    {
        var host = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
        try
        {
            var ex = new Simulation().AssertSqlError("throw 50000, 'x', -1", 2756);
            AreEqual("Invalid value -1 for state. State value must not be less than 0.", ex.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = host;
        }
    }

    [TestMethod]
    public void Calls_LeaveTheCallersCultureInPlace()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        using var connection = new Simulation().CreateOpenConnection();
        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "select 1; select 2";
            _ = command.ExecuteScalar();
            using (var reader = command.ExecuteReader())
            {
                _ = reader.Read();
                _ = reader.NextResult();
            }
            transaction.Commit();
        }

        AreSame(culture, CultureInfo.CurrentCulture);
        AreSame(uiCulture, CultureInfo.CurrentUICulture);
    }

    [TestMethod]
    public void InfoMessageHandlers_RunUnderTheCallersCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        var seen = new List<CultureInfo>();
        connection.InfoMessage += (_, _) => seen.Add(CultureInfo.CurrentCulture);
        using var command = connection.CreateCommand();
        command.CommandText = "print 'a'; select 1; print 'b'";
        _ = command.ExecuteNonQuery();
        using (var reader = command.ExecuteReader())
        {
            while (reader.NextResult())
            {
            }
        }

        HasCount(4, seen);
        foreach (var handlerCulture in seen)
            AreSame(culture, handlerCulture);
    }

    [TestMethod]
    public void ReaderReadToTheEnd_Close_LeavesTheCallersCultureAndCounts()
    {
        var culture = CultureInfo.CurrentCulture;
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (s varchar(10))").ExecuteNonQuery();
        using var command = connection.CreateCommand("insert t (s) values ('a'); select s from t");
        var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
            rows++;
        IsFalse(reader.NextResult());
        reader.Close();

        AreEqual(1, rows);
        AreEqual(1, reader.RecordsAffected);
        IsTrue(reader.IsClosed);
        AreSame(culture, CultureInfo.CurrentCulture);
        AreEqual("a", connection.CreateCommand("select s from t").ExecuteScalar());
    }

    [TestMethod]
    public void ReaderClosedBeforeTheEnd_RunsTheRestUnderTheEngineCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (s varchar(10))").ExecuteNonQuery();
        var seen = new List<CultureInfo>();
        connection.InfoMessage += (_, _) => seen.Add(CultureInfo.CurrentCulture);
        using (var command = connection.CreateCommand("select 1; insert t (s) values (convert(varchar(10), 1.5)); print 'x'"))
        {
            var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            reader.Close();
        }

        AreEqual("1.5", connection.CreateCommand("select s from t").ExecuteScalar());
        HasCount(1, seen);
        AreSame(culture, seen[0]);
        AreSame(culture, CultureInfo.CurrentCulture);
    }

    [TestMethod]
    public void ReaderClosedAfterRunAheadEndedTheBatch_IsClosed()
    {
        var culture = CultureInfo.CurrentCulture;
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 1");
        var reader = command.ExecuteReader();
        reader.Dispose();

        IsTrue(reader.IsClosed);
        reader.Dispose();
        AreSame(culture, CultureInfo.CurrentCulture);
        AreEqual(2, connection.CreateCommand("select 2").ExecuteScalar());
    }

    [TestMethod]
    public void NextResultAfterRunAheadEndedTheBatch_ReturnsFalse_UnderTheCallersCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 1");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
        IsFalse(reader.NextResult());
        AreSame(culture, CultureInfo.CurrentCulture);
    }

    [TestMethod]
    public void KeyInfoReader_Close_TurnsBrowseModeOff()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int primary key, s varchar(10))").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t (id, s) values (1, 'a')").ExecuteNonQuery();
        using (var command = connection.CreateCommand("select id, s from t"))
        {
            var reader = command.ExecuteReader(CommandBehavior.KeyInfo);
            while (reader.Read())
            {
            }

            IsFalse(reader.NextResult());
            reader.Close();
        }

        using var fresh = simulation.CreateOpenConnection();
        AreEqual(UserOptions(fresh), UserOptions(connection));
        using var next = connection.CreateCommand("select id, s from t").ExecuteReader();
        AreEqual(2, next.FieldCount);
    }

    private static string UserOptions(System.Data.Common.DbConnection connection)
    {
        using var reader = connection.CreateCommand("dbcc useroptions").ExecuteReader();
        var options = new List<string>();
        while (reader.Read())
            options.Add(reader.GetString(0));
        return string.Join("|", options);
    }
}
