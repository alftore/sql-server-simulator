using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An index key stored off-row (row-overflow) that an UPDATE moves: the seek
/// cache must not replay the old key from the superseded image, whose chain is
/// given up at commit and may already hold another row's value. Replayed, the
/// row sat in two buckets, so a SNAPSHOT or RCSI ordered read returned it twice
/// and a READ COMMITTED one returned it out of order. SQL Server returns each
/// committed row once, in key order.
/// </summary>
[TestClass]
public sealed class SeekCacheOffRowKeyTests
{
    private static List<string> Keys(DbConnection connection, string sql)
    {
        List<string> keys = [];
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
    }

    private static (Simulation Simulation, DbConnection Reader, DbConnection Writer) OffRowKeys(string isolation)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d");
        _ = simulation.ExecuteNonQuery(isolation == "snapshot"
            ? "alter database d set allow_snapshot_isolation on"
            : "alter database d set read_committed_snapshot on");
        var reader = simulation.CreateOpenConnection();
        reader.ChangeDatabase("d");
        var writer = simulation.CreateOpenConnection();
        writer.ChangeDatabase("d");
        // A key of 800 bytes beside a char(7300) is past the 8,060-byte row,
        // so the row-overflow pass moves the key, the only variable column, off-row.
        _ = writer.CreateCommand("""
            create table t (k varchar(900) not null, f char(7300) not null);
            create index ix on t (k);
            insert t values (replicate('b', 800), 'x'), (replicate('d', 800), 'x'), (replicate('f', 800), 'x');
            """).ExecuteNonQuery();
        if (isolation == "snapshot")
            _ = reader.CreateCommand("set transaction isolation level snapshot").ExecuteNonQuery();
        return (simulation, reader, writer);
    }

    private const string Ordered = "select left(k, 1) from t order by k";

    [TestMethod]
    [DataRow("snapshot")]
    [DataRow("rcsi")]
    public void KeyMovedOffRow_ThenItsChainReused_ReadsEachRowOnce(string isolation)
    {
        var (_, reader, writer) = OffRowKeys(isolation);
        using var _r = reader;
        using var _w = writer;
        CollectionAssert.AreEqual(new[] { "b", "d", "f" }, Keys(reader, Ordered));
        // Moves b's key to h: the old key's off-row chain is given up at commit.
        _ = writer.CreateCommand("update t set k = replicate('h', 800) where k = replicate('b', 800)").ExecuteNonQuery();
        // A new off-row key takes the freed pages before any read replays the update.
        _ = writer.CreateCommand("insert t values (replicate('j', 800), 'x')").ExecuteNonQuery();
        var keys = Keys(reader, Ordered);
        CollectionAssert.AreEqual(new[] { "d", "f", "h", "j" }, keys, string.Join(",", keys));
        AreEqual(4, Convert.ToInt32(reader.CreateCommand("select count(*) from t").ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow("snapshot")]
    [DataRow("rcsi")]
    public void KeyMovedOffRow_NoReuse_ReadsEachRowOnce(string isolation)
    {
        var (_, reader, writer) = OffRowKeys(isolation);
        using var _r = reader;
        using var _w = writer;
        CollectionAssert.AreEqual(new[] { "b", "d", "f" }, Keys(reader, Ordered));
        _ = writer.CreateCommand("update t set k = replicate('h', 800) where k = replicate('b', 800)").ExecuteNonQuery();
        var keys = Keys(reader, Ordered);
        CollectionAssert.AreEqual(new[] { "d", "f", "h" }, keys, string.Join(",", keys));
    }

    [TestMethod]
    public void KeyMovedOffRow_ReadCommitted_ReadsInOrder()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (k varchar(900) not null, f char(7300) not null);
            create index ix on t (k);
            insert t values (replicate('b', 800), 'x'), (replicate('d', 800), 'x'), (replicate('f', 800), 'x');
            """).ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "b", "d", "f" }, Keys(connection, Ordered));
        _ = connection.CreateCommand("update t set k = replicate('h', 800) where k = replicate('b', 800)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (replicate('j', 800), 'x')").ExecuteNonQuery();
        var keys = Keys(connection, Ordered);
        CollectionAssert.AreEqual(new[] { "d", "f", "h", "j" }, keys, string.Join(",", keys));
    }
}
