using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The index-ordered scan keeps no set of the addresses it has walked while
/// they ascend, and builds one from the range walked the first time they do
/// not or the scan's order is swapped under it. Each case answers as the
/// unindexed copy of the same rows, which sorts them instead, whichever side
/// of that line its addresses fall. In Debug the scan also throws if it
/// yields an address twice, so every case here is checked for that as well.
/// </summary>
[TestClass]
public sealed partial class OrderedScanDedupTests
{
    private const int Rows = 5000;

    [GeneratedRegex(@"logical reads (\d+)")]
    private static partial Regex LogicalReads();

    private static void Insert(DbConnection connection, string table, IEnumerable<string> keys)
    {
        foreach (var chunk in keys.Chunk(400))
        {
            var values = new StringBuilder();
            foreach (var key in chunk)
                _ = values.Append(values.Length == 0 ? "" : ", ").Append('(').Append(key).Append(", 'x')");
            _ = connection.CreateCommand($"insert {table} values {values}").ExecuteNonQuery();
        }
    }

    private static List<object?> Page(DbConnection connection, string table, string order, int offset, int fetch) =>
        Extensions.FirstColumn(connection, $"select k from {table} order by k{order} offset {offset} rows fetch next {fetch} rows only");

    /// <summary>The pages of an indexed table and of its unindexed copy, ascending and descending, at a few depths, are equal.</summary>
    private static void AssertPagesMatchTheCopy(DbConnection connection, string table, string copy)
    {
        foreach (var order in new[] { "", " desc" })
        {
            foreach (var offset in new[] { 0, 1, 2500, Rows - 60, Rows - 10 })
            {
                var expected = Page(connection, copy, order, offset, 50);
                var actual = Page(connection, table, order, offset, 50);
                CollectionAssert.AreEqual(expected, actual, $"order{order} offset {offset}");
                HasCount(Math.Min(50, Rows - offset), actual);
            }
        }
    }

    private static DbConnection Tables(string keyType, IReadOnlyList<string> keys)
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table ix (k {keyType} primary key, v char(1) not null); create table plain (k {keyType} not null, v char(1) not null)").ExecuteNonQuery();
        Insert(connection, "ix", keys);
        Insert(connection, "plain", keys);
        return connection;
    }

    private static List<string> Shuffled(IEnumerable<string> keys)
    {
        var all = keys.ToList();
        var random = new Random(20261120);
        for (var i = all.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }
        return all;
    }

    private static IEnumerable<string> Ids() => Enumerable.Range(1, Rows).Select(i => i.ToString(CultureInfo.InvariantCulture));

    [TestMethod]
    public void DeepPage_OverAnAppendOnlyKey_AnswersAsTheUnindexedCopy()
    {
        using var connection = Tables("int", [.. Ids()]);
        AssertPagesMatchTheCopy(connection, "ix", "plain");
        CollectionAssert.AreEqual(Enumerable.Range(2501, 50).Cast<object?>().ToList(), Page(connection, "ix", "", 2500, 50));
    }

    [TestMethod]
    public void DeepPage_OverAKeyWrittenOutOfOrder_AnswersAsTheUnindexedCopy()
    {
        using var connection = Tables("int", Shuffled(Ids()));
        AssertPagesMatchTheCopy(connection, "ix", "plain");

        var random = new Random(7);
        var guids = Enumerable.Range(0, Rows).Select(_ =>
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            return $"'{new Guid(bytes)}'";
        }).ToList();
        using var keyed = Tables("uniqueidentifier", guids);
        AssertPagesMatchTheCopy(keyed, "ix", "plain");
    }

    [TestMethod]
    public void DeepPage_AfterDeletesAndReinserts()
    {
        using var connection = Tables("int", [.. Ids()]);
        foreach (var table in new[] { "ix", "plain" })
        {
            _ = connection.CreateCommand($"delete {table} where k % 3 = 0").ExecuteNonQuery();
            Insert(connection, table, Enumerable.Range(1, Rows / 3).Where(i => i % 2 == 0).Select(i => (i * 3).ToString(CultureInfo.InvariantCulture)));
        }
        var count = Convert.ToInt32(connection.CreateCommand("select count(*) from ix").ExecuteScalar(), CultureInfo.InvariantCulture);
        AreEqual(Convert.ToInt32(connection.CreateCommand("select count(*) from plain").ExecuteScalar(), CultureInfo.InvariantCulture), count);
        foreach (var order in new[] { "", " desc" })
        {
            foreach (var offset in new[] { 0, 1000, 2500, count - 50 })
                CollectionAssert.AreEqual(Page(connection, "plain", order, offset, 50), Page(connection, "ix", order, offset, 50), $"order{order} offset {offset}");
        }
    }

    private static DbDataReader Read(DbConnection connection, string sql, int rows)
    {
        var reader = connection.CreateCommand(sql).ExecuteReader();
        for (var read = 0; read < rows; read++)
            IsTrue(reader.Read());
        return reader;
    }

    private static List<int> Rest(DbDataReader reader)
    {
        List<int> keys = [];
        while (reader.Read())
            keys.Add(reader.GetInt32(0));
        return keys;
    }

    private static Simulation Spaced(string extra = "")
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table big (k int primary key, v char(2000) not null); insert big select value * 2, 'x' from generate_series(1, 2000)");
        if (extra.Length != 0)
            sim.ExecuteBatches(extra);
        return sim;
    }

    /// <summary>
    /// A reader suspended mid-scan while the table gains a key behind its
    /// position and one ahead of it (the order is swapped under the scan, so
    /// the set is built from what it has read): each row once, the key ahead
    /// in its place, the key behind not at all.
    /// </summary>
    [TestMethod]
    [DataRow("", 3, 1001)]
    [DataRow(" desc", 3999, 1001)]
    public void OrderedRead_SuspendedWhileTheTableChanges_ReadsEachRowOnce(string order, int behind, int ahead)
    {
        var sim = Spaced();
        using var connection = sim.CreateOpenConnection();
        using var rows = Read(connection, $"select k, v from big order by k{order}", 2);
        _ = connection.CreateCommand($"set lock_timeout 0; insert big values ({behind}, 'b'), ({ahead}, 'a')").ExecuteNonQuery();
        var keys = Rest(rows);
        HasCount(keys.Count, keys.Distinct().ToList());
        Contains(ahead, keys);
        DoesNotContain(behind, keys);
        HasCount(2000 - 2 + 1, keys);
        List<int> sorted = order.Length == 0 ? [.. keys.Order()] : [.. keys.OrderDescending()];
        CollectionAssert.AreEqual(sorted, keys);
    }

    /// <summary>
    /// A SNAPSHOT reader takes <c>MaterializeOrderedSnapshot</c>, which keeps
    /// no set at all: pins that it reads each row once, and none of another
    /// session's writes since its snapshot, as it does today.
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow(" desc")]
    public void OrderedRead_UnderSnapshot_ReadsEachRowOnce(string order)
    {
        var sim = Spaced("alter database current set allow_snapshot_isolation on");
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var rows = Read(connection, $"set transaction isolation level snapshot; select k, v from big order by k{order}", 2);
        _ = other.CreateCommand("insert big values (3, 'b'), (1001, 'a'), (3999, 'b'); update big set v = 'u' where k = 1500; delete big where k between 1700 and 1709").ExecuteNonQuery();
        var keys = Rest(rows);
        HasCount(keys.Count, keys.Distinct().ToList());
        HasCount(2000 - 2, keys);
        IsFalse(keys.Contains(3) || keys.Contains(1001) || keys.Contains(3999));
        Contains(1706, keys);
        List<int> sorted = order.Length == 0 ? [.. keys.Order()] : [.. keys.OrderDescending()];
        CollectionAssert.AreEqual(sorted, keys);
    }

    private static (int Reads, string Locks) Measure(SimulatedDbConnection connection, string level, string table)
    {
        var reads = 0;
        void Collect(object? sender, SimulatedInfoMessageEventArgs e)
        {
            foreach (var error in e.Errors)
            {
                foreach (Match match in LogicalReads().Matches(error.Message))
                    reads += int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        connection.InfoMessage += Collect;
        try
        {
            _ = connection.CreateCommand($"set transaction isolation level {level}; set statistics io on; begin transaction").ExecuteNonQuery();
            _ = Page(connection, table, "", 2500, 50);
            var locks = string.Join(", ", Extensions.FirstColumn(connection, """
                select concat(resource_type, ' ', request_mode, 'x', count(*))
                from sys.dm_tran_locks where request_session_id = @@spid and resource_type <> 'DATABASE'
                group by resource_type, request_mode order by resource_type, request_mode
                """));
            _ = connection.CreateCommand("set statistics io off; commit").ExecuteNonQuery();
            return (reads, locks);
        }
        finally
        {
            connection.InfoMessage -= Collect;
        }
    }

    /// <summary>
    /// The deep page reads and locks the same rows whether its addresses
    /// ascend (no set) or not (a set from the second address): the table
    /// whose keys are written in swapped pairs puts the same rows on the same
    /// pages with each pair's slots reversed.
    /// </summary>
    [TestMethod]
    [DataRow("repeatable read")]
    [DataRow("read committed")]
    public void StatisticsIo_AndLocks_UnchangedByTheDedup(string level)
    {
        var swapped = Enumerable.Range(1, Rows / 2).SelectMany(pair => new[] { pair * 2, (pair * 2) - 1 }).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table appended (k int primary key, v char(1) not null); create table paired (k int primary key, v char(1) not null)").ExecuteNonQuery();
        Insert(connection, "appended", Ids());
        Insert(connection, "paired", swapped);
        var (readsAppended, locksAppended) = Measure(connection, level, "appended");
        var (readsPaired, locksPaired) = Measure(connection, level, "paired");
        IsGreaterThan(0, readsAppended);
        AreEqual(readsAppended, readsPaired);
        AreEqual(locksAppended, locksPaired);
        AreEqual(level == "repeatable read", locksAppended.Contains("KEY S", StringComparison.Ordinal));
    }
}
