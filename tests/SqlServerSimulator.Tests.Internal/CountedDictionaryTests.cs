using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="CountedDictionary{TKey, TValue}"/>'s count, which the emptiness
/// guards on the lock and definition paths read in place of an enumeration:
/// it follows every write, and never reads empty while an entry is present.
/// </summary>
[TestClass]
public sealed class CountedDictionaryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void EveryWrite_MovesTheCount()
    {
        var dictionary = new CountedDictionary<string, int>(1, 0, StringComparer.OrdinalIgnoreCase);
        IsTrue(dictionary.IsEmptyLockFree());
        AreEqual(1, dictionary.GetOrAdd("a", static (_, n) => n, 1));
        IsFalse(dictionary.IsEmptyLockFree());
        // A key that is there answers its value, and adds nothing.
        AreEqual(1, dictionary.GetOrAdd("A", static _ => 5));
        dictionary.Set("a", 2);
        dictionary.Set("b", 3);
        HasCount(2, dictionary.ToArray());
        IsFalse(dictionary.TryRemove(KeyValuePair.Create("a", 1)));
        IsTrue(dictionary.TryRemove(KeyValuePair.Create("a", 2)));
        IsFalse(dictionary.TryRemove("a", out _));
        IsTrue(dictionary.TryRemove("b", out var b));
        AreEqual(3, b);
        IsTrue(dictionary.IsEmptyLockFree());
        dictionary.Set("c", 1);
        dictionary.Set("d", 1);
        dictionary.Clear();
        IsTrue(dictionary.IsEmptyLockFree());
        IsEmpty(dictionary.ToArray());
    }

    [TestMethod]
    public void AnEntryAnAddPublished_IsNeverReadAsEmptyByALaterReader()
    {
        var dictionary = new CountedDictionary<int, int>(1, 0);
        for (var round = 0; round < 2_000; round++)
        {
            var published = 0;
            var reader = Task.Run(() =>
            {
                while (Volatile.Read(ref published) == 0)
                    _ = Thread.Yield();
                return dictionary.IsEmptyLockFree();
            }, TestContext.CancellationToken);
            _ = dictionary.GetOrAdd(round, static (key, _) => key, 0);
            Volatile.Write(ref published, 1);
            IsFalse(reader.GetAwaiter().GetResult());
            IsTrue(dictionary.TryRemove(round, out _));
            IsTrue(dictionary.IsEmptyLockFree());
        }
    }

    [TestMethod]
    public void ConcurrentWriters_LeaveTheCountWhatTheEntriesAre()
    {
        var dictionary = new CountedDictionary<int, int>();
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 20_000; i++)
            {
                var key = random.Next(6);
                switch (random.Next(5))
                {
                    case 0: _ = dictionary.GetOrAdd(key, static k => k); break;
                    case 1: dictionary.Set(key, i); break;
                    case 2: _ = dictionary.TryRemove(key, out _); break;
                    case 3: _ = dictionary.TryRemove(KeyValuePair.Create(key, key)); break;
                    default: _ = dictionary.IsEmptyLockFree(); break;
                }
            }
        }, TestContext.CancellationToken)).ToArray();
        Task.WaitAll(workers, TestContext.CancellationToken);
        AreEqual(dictionary.ToArray().Length == 0, dictionary.IsEmptyLockFree());
        foreach (var (key, _) in dictionary.ToArray())
            _ = dictionary.TryRemove(key, out _);
        IsTrue(dictionary.IsEmptyLockFree());
    }
}
