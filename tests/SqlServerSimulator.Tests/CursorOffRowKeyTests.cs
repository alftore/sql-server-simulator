using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// A cursor ordered by an index key stored off-row (row-overflow) while
/// another connection moves a key and the moved key's old chain is given up
/// at commit and reused. The DYNAMIC cursor's kept order
/// (<c>Selection.CursorOrder</c>) judges each journaled update by decoding the
/// old and new key from the journaled images when it next fetches; an old
/// image naming a reused chain must not read as "unchanged".
/// </summary>
[TestClass]
public sealed class CursorOffRowKeyTests
{
    private static (DbConnection Reader, DbConnection Writer) Setup()
    {
        var simulation = new Simulation();
        var reader = simulation.CreateOpenConnection();
        var writer = simulation.CreateOpenConnection();
        // An 800-byte key beside a char(7300) is past the 8,060-byte row, so
        // the row-overflow pass moves the key, the only variable column, off-row.
        _ = writer.CreateCommand("""
            create table t (id int not null primary key nonclustered, k varchar(900) not null, f char(7300) not null);
            create index ix on t (k);
            insert t values (1, replicate('b', 800), 'x'), (2, replicate('d', 800), 'x'), (3, replicate('f', 800), 'x');
            """).ExecuteNonQuery();
        return (reader, writer);
    }

    private static string? Fetch(DbConnection reader)
    {
        using var r = reader.CreateCommand("fetch next from c").ExecuteReader();
        return r.Read() ? r.GetString(0) : null;
    }

    private static List<string> FetchRest(DbConnection reader)
    {
        List<string> keys = [];
        while (Fetch(reader) is { } key)
            keys.Add(key);
        return keys;
    }

    /// <summary>
    /// The row a DYNAMIC cursor read first has its key moved ahead (b to h);
    /// a second update rewrites the same row, so its new key chain takes the
    /// pages b's old chain gave up, writing h's value there. A DYNAMIC cursor
    /// meets a row whose key an UPDATE moved ahead of it again
    /// (docs/claude/cursors.md), so the rest is d, f, h.
    /// </summary>
    [TestMethod]
    [DataRow("dynamic")]
    [DataRow("forward_only")]
    [DataRow("fast_forward")]
    public void KeyMovedAhead_OldChainReusedBySameValue_MeetsRowAgain(string kind)
    {
        var (reader, writer) = Setup();
        using var _r = reader;
        using var _w = writer;
        var options = kind switch { "dynamic" => "dynamic", "fast_forward" => "fast_forward", _ => "forward_only" };
        _ = reader.CreateCommand($"declare c cursor global {options} for select left(k, 1) from t order by k; open c;").ExecuteNonQuery();
        Assert.AreEqual("b", Fetch(reader));
        _ = writer.CreateCommand("update t set k = replicate('h', 800) where id = 1").ExecuteNonQuery();
        _ = writer.CreateCommand("update t set k = replicate('h', 800), f = 'y' where id = 1").ExecuteNonQuery();
        var rest = FetchRest(reader);
        CollectionAssert.AreEqual(new[] { "d", "f", "h" }, rest, string.Join(",", rest));
    }

    /// <summary>The same through the cursor's own positioned updates (FOR UPDATE).</summary>
    [TestMethod]
    public void KeyMovedAheadWhereCurrentOf_OldChainReusedBySameValue_MeetsRowAgain()
    {
        var (reader, writer) = Setup();
        using var _r = reader;
        using var _w = writer;
        _ = reader.CreateCommand("declare c cursor global dynamic for select left(k, 1) from t order by k for update; open c;").ExecuteNonQuery();
        Assert.AreEqual("b", Fetch(reader));
        _ = reader.CreateCommand("update t set k = replicate('h', 800) where current of c").ExecuteNonQuery();
        _ = reader.CreateCommand("update t set k = replicate('h', 800), f = 'y' where current of c").ExecuteNonQuery();
        var rest = FetchRest(reader);
        CollectionAssert.AreEqual(new[] { "d", "f", "h" }, rest, string.Join(",", rest));
    }

    /// <summary>The control: the key moves ahead, nothing reuses the chain.</summary>
    [TestMethod]
    [DataRow("dynamic")]
    [DataRow("fast_forward")]
    public void KeyMovedAhead_NoReuse_MeetsRowAgain(string kind)
    {
        var (reader, writer) = Setup();
        using var _r = reader;
        using var _w = writer;
        _ = reader.CreateCommand($"declare c cursor global {kind} for select left(k, 1) from t order by k; open c;").ExecuteNonQuery();
        Assert.AreEqual("b", Fetch(reader));
        _ = writer.CreateCommand("update t set k = replicate('h', 800) where id = 1").ExecuteNonQuery();
        var rest = FetchRest(reader);
        CollectionAssert.AreEqual(new[] { "d", "f", "h" }, rest, string.Join(",", rest));
    }

    /// <summary>
    /// The reusing write is an insert, which the kept order never survives,
    /// so the order is read again whatever the images decode to.
    /// </summary>
    [TestMethod]
    [DataRow("dynamic")]
    [DataRow("fast_forward")]
    public void KeyMovedAhead_ChainReusedByInsert_MeetsRowAgain(string kind)
    {
        var (reader, writer) = Setup();
        using var _r = reader;
        using var _w = writer;
        _ = reader.CreateCommand($"declare c cursor global {kind} for select left(k, 1) from t order by k; open c;").ExecuteNonQuery();
        Assert.AreEqual("b", Fetch(reader));
        _ = writer.CreateCommand("update t set k = replicate('h', 800) where id = 1").ExecuteNonQuery();
        _ = writer.CreateCommand("insert t values (4, replicate('h', 800), 'x')").ExecuteNonQuery();
        var rest = FetchRest(reader);
        CollectionAssert.AreEqual(new[] { "d", "f", "h", "h" }, rest, string.Join(",", rest));
    }

    /// <summary>
    /// KEYSET: membership frozen at OPEN, values re-read live. Row 1's key
    /// is changed, but the member keys on the nonclustered primary key
    /// (id), so it still fetches, with its new value.
    /// </summary>
    [TestMethod]
    [DataRow("keyset")]
    [DataRow("static")]
    public void KeyMovedAhead_OldChainReused_KeysetAndStatic(string kind)
    {
        var (reader, writer) = Setup();
        using var _r = reader;
        using var _w = writer;
        _ = reader.CreateCommand($"declare c cursor global {kind} for select left(k, 1) from t order by k; open c;").ExecuteNonQuery();
        Assert.AreEqual("b", Fetch(reader));
        _ = writer.CreateCommand("update t set k = replicate('h', 800) where id = 1").ExecuteNonQuery();
        _ = writer.CreateCommand("update t set k = replicate('h', 800), f = 'y' where id = 1").ExecuteNonQuery();
        var rest = FetchRest(reader);
        CollectionAssert.AreEqual(new[] { "d", "f" }, rest, string.Join(",", rest));
    }
}
