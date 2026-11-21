using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

/// <summary>
/// A process-wide cache under every <see cref="Simulation"/>'s own, for what
/// a statement's text alone decides: the token memo's sequences
/// (<see cref="Parser.TokenMemo"/>) and Query Store's statement shapes
/// (<see cref="QueryStoreShape"/>). A text one simulation has tokenized is
/// then not tokenized again by the next simulation running it — a test
/// suite's fresh simulation per test runs the same application statements
/// as the last one did.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a pure function of the key goes in.</b> The key holds every input
/// the value is computed from — the text, the collation (by reference: they
/// are interned process-wide), the compatibility level and
/// <c>QUOTED_IDENTIFIER</c> — and the value holds nothing of the simulation
/// that computed it: no schema object, no setting, no session. So an entry
/// can't be stale, nothing needs to clear it (only tests do, to start from
/// an empty tier), and which simulation put it there
/// can't be told from what it answers. Each tier's Debug audit computes a
/// sampled hit again and throws on a difference.
/// </para>
/// <para>
/// <b>Bounded by count and by text.</b> At most <c>capacity</c> entries and
/// <c>textBudget</c> characters of key text (the weight
/// <see cref="BoundedCache{TKey, TValue}"/> sweeps by), with the per-
/// simulation caches' recency eviction and second-arrival admission; a text
/// longer than <c>maxTextLength</c> is never kept here, so a seed script or
/// an imported database's batch isn't held for the life of the process. The
/// bounds aren't tunable: a simulation still keeps whatever it runs in its
/// own caches, so a miss here costs only what a fresh simulation paid before
/// the tier existed.
/// </para>
/// <para>
/// <b>Concurrency.</b> Values are built whole before they're added and never
/// written after, apart from the idempotent lazy fields
/// <see cref="Parser.TokenMemo"/> and <see cref="QueryStoreShape"/> document;
/// a lookup takes no lock (<see cref="BoundedCache{TKey, TValue}"/>), and a
/// sweep runs on one thread at a time beside lookups and inserts.
/// </para>
/// </remarks>
internal sealed class SharedTextTier<TKey, TValue>(int capacity, long textBudget, int maxTextLength, IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private readonly BoundedCache<TKey, TValue> entries = new(capacity, comparer, textBudget);

    /// <summary>The longest text the tier keeps, in characters.</summary>
    public int MaxTextLength => maxTextLength;

    /// <summary>Test-observable: entries held.</summary>
    public int Count => this.entries.Count;

    /// <summary>Test-observable: characters of text the entries held are keyed by.</summary>
    public long TextLength => this.entries.Weight;

    /// <summary>Test-observable: entries the sweeps have evicted.</summary>
    public long Evictions => Volatile.Read(ref this.entries.Evictions);

    /// <summary>Test-only: forgets every entry, so a test starts from an empty tier.</summary>
    internal void Clear() => this.entries.Clear();

    /// <summary>Whether a text of <paramref name="length"/> characters may be kept here.</summary>
    public bool Keeps(int length) => length <= maxTextLength;

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => this.entries.TryGetValue(key, out value);

    /// <summary>A lookup through a key form the comparer accepts in place of <typeparamref name="TKey"/>.</summary>
    public bool TryGetValue<TAlternate>(TAlternate key, [MaybeNullWhen(false)] out TValue value)
        where TAlternate : notnull, allows ref struct =>
        this.entries.TryGetValue(key, out value);

    /// <summary>
    /// Keeps <paramref name="value"/>, computed from <paramref name="key"/>
    /// alone, whose text is <paramref name="textLength"/> characters long;
    /// nothing when the text is too long, the key is already held, or a full
    /// tier refuses it.
    /// </summary>
    public bool TryAdd(TKey key, int textLength, TValue value) =>
        this.Keeps(textLength) && this.entries.TryAdd(key, value, weight: int.Max(textLength, 1));

#if DEBUG
    private long audited;

    /// <summary>
    /// Whether the hit a caller just had is one its Debug audit checks: the
    /// first, then each at a power-of-two count, as <c>Add</c>'s fast path
    /// samples its own.
    /// </summary>
    public bool AuditsThisHit() => System.Numerics.BitOperations.IsPow2(Interlocked.Increment(ref this.audited));
#endif
}
