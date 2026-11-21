using System.Runtime.CompilerServices;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A command's timeout is armed on a timer the connection owns, which must not
/// keep the connection alive: one abandoned after a timed command is still
/// finalized and reclaimed.
/// </summary>
[TestClass]
public sealed class DeadlineTimerRetentionTests
{
    // Not inlined, so no caller frame keeps the connection reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SessionToken LeakAfterATimedCommand(Simulation simulation)
    {
        // Abandoning the connection undisposed is the scenario under test.
        var connection = simulation.CreateDbConnection();
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select 1";
            command.CommandTimeout = 600;
            AreEqual(1, command.ExecuteScalar());
        }
        return connection.Session;
    }

    [TestMethod]
    public void AbandonedConnectionAfterATimedCommand_IsReclaimed()
    {
        var simulation = new Simulation();
        var session = LeakAfterATimedCommand(simulation);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        AreEqual(1, simulation.ReclaimAbandonedSessions());
        IsTrue(session.Reclaimed);
    }
}
