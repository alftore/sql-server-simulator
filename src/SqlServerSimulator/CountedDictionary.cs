using System.Collections;
using System.Collections.Concurrent;

namespace SqlServerSimulator;

/// <summary>
/// A <see cref="ConcurrentDictionary{TKey, TValue}"/> that keeps the number of
/// its entries beside it, for the guard a hot path asks before a lookup: "is
/// there anything here at all" (<see cref="IsEmptyLockFree"/>), which the
/// dictionary itself answers by starting an enumeration (an enumerator object
/// per ask) or by taking every bucket lock (<c>IsEmpty</c>, whenever the answer
/// is yes, the common one).
/// <para>
/// <b>Every write goes through this class's methods</b>, which are the only
/// ones that touch the dictionary, so the count can't drift: nothing here
/// hands out the dictionary, and a new write path is a new method here.
/// </para>
/// <para>
/// <b>The count is never below the number of entries.</b> A writer raises it
/// <em>before</em> it adds an entry and lowers it <em>after</em> it removed one,
/// each a full fence, so the count read by <see cref="IsEmptyLockFree"/> is 0
/// only when no entry has been published and none is still present: a reader
/// that begins after an add returned (a lock taken, then another session
/// resolving the name, the order <c>LockDefinitionName</c> and
/// <c>AwaitDefinitionName</c> rely on) reads the raised count and goes on to the
/// lookup. What the count can be is too high: while a writer is between its
/// increment and its add, after a failed add, or between a remove and its
/// decrement. A guard that answers "not empty" then only costs the lookup it
/// guards, which is safe on its own, where the enumerator it replaces could
/// equally answer "empty" for an add still in flight. The answer is a hint,
/// never a verdict.
/// </para>
/// <para>
/// In Debug every write also runs under a gate, and <see cref="IsEmptyLockFree"/>
/// compares the count with the dictionary's own on every power-of-two call,
/// which the gate makes exact (the other audits sample the same way).
/// </para>
/// </summary>
internal sealed class CountedDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> entries;

    /// <summary>The entries <see cref="entries"/> holds, or is about to or has just stopped holding; see the class remarks.</summary>
    private int count;

#if DEBUG
    private readonly Lock auditGate = new();
    private int audits;
#endif

    public CountedDictionary() => this.entries = new();

    public CountedDictionary(int concurrencyLevel, int capacity, IEqualityComparer<TKey>? comparer = null) =>
        this.entries = new(concurrencyLevel, capacity, comparer);

    /// <summary>
    /// Whether the dictionary holds no entry: one volatile read. A hint before a
    /// lookup that is safe by itself; true only when nothing is present, and
    /// false when something may be (see the class remarks).
    /// </summary>
    public bool IsEmptyLockFree()
    {
#if DEBUG
        if (System.Numerics.BitOperations.IsPow2(Interlocked.Increment(ref this.audits)))
        {
            lock (this.auditGate)
            {
#pragma warning disable SSS012 // The audit wants the count of a dictionary no writer is in.
                var actual = this.entries.Count;
#pragma warning restore SSS012
                if (Volatile.Read(ref this.count) != actual)
                    throw new InvalidOperationException($"A CountedDictionary's count is {this.count} where its dictionary holds {actual} entries.");
            }
        }
#endif
        return Volatile.Read(ref this.count) == 0;
    }

    public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value) => this.entries.TryGetValue(key, out value);

    public bool ContainsKey(TKey key) => this.entries.ContainsKey(key);

    /// <summary>
    /// The value under <paramref name="key"/>, made by <paramref name="valueFactory"/> and added when there is none.
    /// As <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd{TArg}(TKey, Func{TKey, TArg, TValue}, TArg)"/>,
    /// the factory may run for a value another thread's add then wins over.
    /// </summary>
    public TValue GetOrAdd<TArg>(TKey key, Func<TKey, TArg, TValue> valueFactory, TArg factoryArgument)
    {
        while (true)
        {
            if (this.entries.TryGetValue(key, out var existing))
                return existing;
            var made = valueFactory(key, factoryArgument);
            if (this.TryAdd(key, made))
                return made;
        }
    }

    /// <inheritdoc cref="GetOrAdd{TArg}(TKey, Func{TKey, TArg, TValue}, TArg)"/>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        while (true)
        {
            if (this.entries.TryGetValue(key, out var existing))
                return existing;
            var made = valueFactory(key);
            if (this.TryAdd(key, made))
                return made;
        }
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing the entry there.</summary>
    public void Set(TKey key, TValue value)
    {
        while (true)
        {
            if (this.entries.TryGetValue(key, out var existing))
            {
                if (this.entries.TryUpdate(key, value, existing))
                    return;
            }
            else if (this.TryAdd(key, value))
            {
                return;
            }
        }
    }

    /// <summary>Removes the entry under <paramref name="key"/>, giving the value it held.</summary>
    public bool TryRemove(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value)
    {
#if DEBUG
        lock (this.auditGate)
#endif
        {
            if (!this.entries.TryRemove(key, out value))
                return false;
            _ = Interlocked.Decrement(ref this.count);
            return true;
        }
    }

    /// <summary>Removes the entry under the pair's key when it still holds the pair's value.</summary>
    public bool TryRemove(KeyValuePair<TKey, TValue> item)
    {
#if DEBUG
        lock (this.auditGate)
#endif
        {
            if (!this.entries.TryRemove(item))
                return false;
            _ = Interlocked.Decrement(ref this.count);
            return true;
        }
    }

    /// <summary>
    /// Removes every entry, one at a time: a concurrent add may survive it, and the
    /// count stays what the entries are.
    /// </summary>
    public void Clear()
    {
        foreach (var (key, _) in this.entries)
            _ = this.TryRemove(key, out _);
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => this.entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <summary>Adds an entry for a key that has none, raising the count first so it is never below the entries.</summary>
    private bool TryAdd(TKey key, TValue value)
    {
#if DEBUG
        lock (this.auditGate)
#endif
        {
            _ = Interlocked.Increment(ref this.count);
            if (this.entries.TryAdd(key, value))
                return true;
            _ = Interlocked.Decrement(ref this.count);
            return false;
        }
    }
}
