using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

internal class WireItem
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public DateTime? Stocked { get; set; }
}

internal sealed class WireEventsContext(Simulation simulation, int port) : TestDbContext(simulation)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSqlServer($"Server=127.0.0.1,{port};User ID=sa;Password=anything;TrustServerCertificate=True;Pooling=False;Connect Timeout=15");
}

internal sealed class WireDbContext(string connectionString) : DbContext
{
    public DbSet<WireItem> Items => this.Set<WireItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSqlServer(connectionString);
}

/// <summary>
/// The TDS endpoint's crowning oracle: EF Core's genuine SQL Server provider
/// talking to the simulator over loopback TCP through vanilla
/// <c>UseSqlServer</c> — a real <c>SqlConnection</c> end to end, no
/// simulator-specific adapter anywhere in the stack.
/// </summary>
[TestClass]
public sealed class EFCoreOverWire
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// EF Core sets a <c>datetime2(3)</c> column's parameter <c>Scale</c> to 3, and
    /// SqlClient truncates the value to it before sending, so a value 0.5 ms past a
    /// millisecond stores as the millisecond below. The in-process provider must
    /// store the same, from the same parameter.
    /// </summary>
    [TestMethod]
    public async Task Insert_ScaledDateTime_StoresAsOverTheWire()
    {
        var updated = new DateTime(2026, 5, 4, 13, 45, 30, 100).AddTicks(5_000);
        var overWire = TestDbContext.CreateEventsSimulation();
        await using (var listener = await overWire.ListenLocalAsync(0, TestContext.CancellationToken))
        {
            using var context = new WireEventsContext(overWire, listener.Port);
            _ = context.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Updated = updated });
            _ = await context.SaveChangesAsync(TestContext.CancellationToken);
        }
        using var inProcess = new TestDbContext(TestDbContext.CreateEventsSimulation());
        _ = inProcess.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Updated = updated });
        _ = await inProcess.SaveChangesAsync(TestContext.CancellationToken);

        using var wireConnection = overWire.CreateOpenConnection();
        using var inProcessConnection = inProcess.Simulation.CreateOpenConnection();
        const string Stored = "select convert(varchar(30), Updated, 121) from Events";
        Assert.AreEqual("2026-05-04 13:45:30.100", wireConnection.CreateCommand(Stored).ExecuteScalar());
        Assert.AreEqual("2026-05-04 13:45:30.100", inProcessConnection.CreateCommand(Stored).ExecuteScalar());
    }

    [TestMethod]
    public async Task VanillaUseSqlServer_OverTcp_FullCrudRoundTrip()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;TrustServerCertificate=True;Connect Timeout=15";

        using (var context = new WireDbContext(connectionString))
        {
            _ = await context.Database.EnsureCreatedAsync(TestContext.CancellationToken);
            context.Items.AddRange(
                new WireItem { Name = "widget", Price = 19.99m, Stocked = new DateTime(2026, 7, 1, 8, 30, 0) },
                new WireItem { Name = "gadget", Price = 4.50m },
                new WireItem { Name = "gizmo", Price = 100m, Stocked = new DateTime(2026, 6, 15, 12, 0, 0) });
            Assert.AreEqual(3, await context.SaveChangesAsync(TestContext.CancellationToken));
        }

        using (var context = new WireDbContext(connectionString))
        {
            var stocked = await context.Items
                .Where(item => item.Stocked != null && item.Price < 50m)
                .Select(item => item.Name)
                .ToListAsync(TestContext.CancellationToken);
            Assert.HasCount(1, stocked);
            Assert.AreEqual("widget", stocked[0]);

            var total = await context.Items.SumAsync(item => item.Price, TestContext.CancellationToken);
            Assert.AreEqual(124.49m, total);
        }

        using (var context = new WireDbContext(connectionString))
        {
            var gadget = await context.Items.SingleAsync(item => item.Name == "gadget", TestContext.CancellationToken);
            gadget.Price = 5.25m;
            _ = await context.SaveChangesAsync(TestContext.CancellationToken);

            var gizmo = await context.Items.SingleAsync(item => item.Name == "gizmo", TestContext.CancellationToken);
            _ = context.Items.Remove(gizmo);
            _ = await context.SaveChangesAsync(TestContext.CancellationToken);
        }

        using (var context = new WireDbContext(connectionString))
        {
            var remaining = await context.Items.OrderBy(item => item.Name).ToListAsync(TestContext.CancellationToken);
            Assert.HasCount(2, remaining);
            Assert.AreEqual("gadget", remaining[0].Name);
            Assert.AreEqual(5.25m, remaining[0].Price);
            Assert.AreEqual("widget", remaining[1].Name);
        }
    }

    [TestMethod]
    public async Task VanillaUseSqlServer_OverTcp_ExplicitTransactionRollback()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;TrustServerCertificate=True;Connect Timeout=15";

        using var context = new WireDbContext(connectionString);
        _ = await context.Database.EnsureCreatedAsync(TestContext.CancellationToken);

        using (var transaction = await context.Database.BeginTransactionAsync(TestContext.CancellationToken))
        {
            _ = context.Items.Add(new WireItem { Name = "phantom", Price = 1m });
            _ = await context.SaveChangesAsync(TestContext.CancellationToken);
            await transaction.RollbackAsync(TestContext.CancellationToken);
        }

        Assert.AreEqual(0, await context.Items.CountAsync(TestContext.CancellationToken));
    }
}
