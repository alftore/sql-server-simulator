using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An insert of a key above the last one keeps the index's memoized key order
/// by appending to it (<c>HeapSeekCache.CacheEntry.KeyOrder</c>), so what an
/// ordered read returns after one is what it returns with no index to keep it
/// for, and a reader holding the order's run reads it intact while another
/// request appends. See <c>docs/claude/indexes.md</c>.
/// </summary>
[TestClass]
public sealed class KeyOrderAppendTests
{
    /// <summary>
    /// Writes of every kind around the end of the key order, each followed by
    /// the ordered reads that use it, answer as the same table with no index.
    /// </summary>
    [TestMethod]
    public void KeyOrderAcrossWrites_AnswersAsTheUnindexedCopy()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table seeked (id int primary key, v int); create table scanned (id int, v int)",
            "insert seeked select value * 10, 0 from generate_series(1, 20); insert scanned select value * 10, 0 from generate_series(1, 20)");
        using var connection = sim.CreateOpenConnection();
        var keys = Enumerable.Range(1, 20).Select(i => i * 10).ToList();
        var random = new Random(17);
        var queries = new[]
        {
            "select id from {0} order by id",
            "select id from {0} order by id desc",
            "select id from {0} order by id desc offset 3 rows fetch next 5 rows only",
            "select top (1) id from {0} order by id desc",
        };

        for (var step = 0; step < 300; step++)
        {
            var write = "select 1";
            switch (random.Next(8))
            {
                case 0:
                    var above = keys.Max() + 1 + random.Next(5);
                    keys.Add(above);
                    write = $"insert {{0}} values ({above}, 1)";
                    break;
                case 1:
                    var below = keys.Min() - 1 - random.Next(5);
                    keys.Add(below);
                    write = $"insert {{0}} values ({below}, 1)";
                    break;
                case 2:
                    var inside = ((keys.Min() + keys.Max()) / 2) + random.Next(-3, 4);
                    if (!keys.Contains(inside))
                    {
                        keys.Add(inside);
                        write = $"insert {{0}} values ({inside}, 1)";
                    }
                    break;
                case 3 when keys.Count > 3:
                    var first = keys.Min();
                    _ = keys.Remove(first);
                    write = $"delete {{0}} where id = {first}";
                    break;
                case 4 when keys.Count > 3:
                    var last = keys.Max();
                    _ = keys.Remove(last);
                    write = $"delete {{0}} where id = {last}";
                    break;
                case 5 when keys.Count > 3:
                    var middle = keys.Order().ElementAt(keys.Count / 2);
                    _ = keys.Remove(middle);
                    write = $"delete {{0}} where id = {middle}";
                    break;
                case 6:
                    write = $"begin tran; insert {{0}} values ({keys.Max() + 1}, 2); rollback";
                    break;
                case 7 when keys.Count > 3:
                    var moved = keys[random.Next(keys.Count)];
                    var top = keys.Max() + 1;
                    _ = keys.Remove(moved);
                    keys.Add(top);
                    write = $"update {{0}} set id = {top} where id = {moved}";
                    break;
                default:
                    break;
            }

            foreach (var table in new[] { "seeked", "scanned" })
                _ = connection.CreateCommand(string.Format(write, table)).ExecuteNonQuery();
            foreach (var query in queries)
            {
                var expected = Extensions.FirstColumn(connection, string.Format(query, "scanned"));
                var actual = Extensions.FirstColumn(connection, string.Format(query, "seeked"));
                CollectionAssert.AreEqual(expected, actual, $"after step {step} ({write}): {query}");
            }
        }
    }

    /// <summary>
    /// A reader holding the key order's run while another request of its
    /// session inserts above the last key reads on the run it holds, without
    /// a row lost or listed twice and as it did before the append kept the
    /// order (the new keys, past the run, are not in it), and a read after it
    /// sees every key once.
    /// </summary>
    [TestMethod]
    public void OpenReader_KeepsItsRows_WhileAnotherRequestAppends()
    {
        const int Rows = 2000;
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table big (k int primary key, v char(2000) not null); insert big select value, 'x' from generate_series(1, {Rows})");
        using var connection = sim.CreateOpenConnection();
        _ = Extensions.FirstColumn(connection, "select k from big order by k");
        using var reader = connection.CreateCommand("select k from big order by k").ExecuteReader();
        var keys = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            IsTrue(reader.Read());
            keys.Add(reader.GetInt32(0));
        }

        _ = connection.CreateCommand($"insert big select value, 'y' from generate_series({Rows + 1}, {Rows + 5})").ExecuteNonQuery();
        while (reader.Read())
            keys.Add(reader.GetInt32(0));

        CollectionAssert.AreEqual(Enumerable.Range(1, Rows).ToList(), keys);
        var again = Extensions.FirstColumn(connection, "select k from big order by k");
        CollectionAssert.AreEqual(Enumerable.Range(1, Rows + 5).Cast<object?>().ToList(), again);
    }

    /// <summary>An identity key only ever grows, so every insert is an append; the ordered top after it reads the row just inserted.</summary>
    [TestMethod]
    public void IdentityTable_InsertThenOrderedTop_ReadsTheNewRow()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar<int>("""
            create table t (id int identity primary key, v int not null);
            declare @i int = 0, @bad int = 0, @new int, @top int;
            while @i < 1000
            begin
                insert t (v) values (@i);
                set @new = scope_identity();
                select top (1) @top = id from t order by id desc;
                if @top <> @new set @bad += 1;
                set @i += 1;
            end;
            select @bad;
            """));
        AreEqual(1000, sim.ExecuteScalar<int>("select count(*) from t"));
    }
}
