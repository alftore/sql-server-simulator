using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-<see cref="Simulation"/> memo of tokenized command texts: the
/// <see cref="Token"/> sequence a <see cref="ParserContext"/> walks, keyed by
/// everything <see cref="Tokenizer.NextToken"/> reads. A repeat execution of
/// the same text re-parses, but scans no characters and allocates no tokens.
/// <para>
/// This is the plan cache's counterpart for the statements that have no plan
/// object. A <see cref="Selection"/> can be cached because SELECT's parse
/// produces a re-executable artifact; INSERT / UPDATE / DELETE / MERGE parse
/// and execute in one interleaved pass, so there is nothing to store and
/// replay (see <c>docs/claude/plan-cache.md</c>). Their token sequence,
/// though, is the same on every execution, and is the one part of the
/// front half that can be shared with no replay-safety question at all.
/// </para>
/// <para>
/// <b>No invalidation.</b> Tokenization is a pure function of the four key
/// components, so an entry can never go stale — unlike a cached plan, which
/// stamps <see cref="Simulation.SchemaVersion"/> because it holds resolved
/// schema objects. A memo holds only tokens, which name schema objects
/// without resolving them.
/// </para>
/// <para>
/// <b>A process-wide tier under it.</b> Being a pure function of the key, a
/// sequence one simulation tokenized serves every other simulation running
/// the same text under the same inputs: <see cref="Shared"/>, which a
/// simulation's own memo asks on its miss (<see cref="TryGetShared"/>). The
/// per-simulation memo and its counters go on as if the tier weren't there —
/// a shared sequence enters this memo where the parse tokenizing it live
/// would have published it (<see cref="Adopt"/>) — so the tier saves the
/// tokenizing and nothing else.
/// </para>
/// </summary>
internal sealed class TokenMemo
{
    /// <summary>
    /// The plan cache's bound, past which the least recently tokenized texts
    /// are forgotten (see <see cref="BoundedCache{TKey, TValue}"/>).
    /// </summary>
    private const int Capacity = 1024;

    /// <summary>
    /// The longest text the process-wide tier keeps, in characters; a longer
    /// one is memoized per simulation only.
    /// </summary>
    internal const int MaxSharedTextLength = 32_768;

    /// <summary>The characters of text the process-wide tier keeps sequences for, all told.</summary>
    internal const long SharedTextBudget = 1 << 20;

    private readonly BoundedCache<TokenMemoKey, Token[]> entries = new(Capacity);

    /// <summary>
    /// Every simulation's sequences, by the same key: a sequence holds tokens
    /// and the command text they slice, which are the same whichever
    /// simulation tokenized them (see <see cref="SharedTextTier{TKey, TValue}"/>).
    /// </summary>
    internal static readonly SharedTextTier<TokenMemoKey, Token[]> Shared = new(Capacity, SharedTextBudget, MaxSharedTextLength);

    /// <summary>Test-observable: memo lookups that found a token sequence.</summary>
    public long Hits;

    /// <summary>Test-observable: memo lookups that had to tokenize live.</summary>
    public long Misses;

    /// <summary>
    /// Test-observable: misses the process-wide tier served, each also
    /// counted in <see cref="Misses"/>.
    /// </summary>
    public long SharedHits;

    /// <summary>Test-observable: live entry count.</summary>
    public int Count => this.entries.Count;

    /// <summary>
    /// The stored token sequence for <paramref name="key"/>, or
    /// <see langword="null"/> when the text hasn't been tokenized under these
    /// inputs yet. The returned array is shared with every other execution of
    /// the same text and must not be mutated.
    /// </summary>
    public Token[]? TryGet(in TokenMemoKey key)
    {
        if (this.entries.TryGetValue(key, out var tokens))
        {
            _ = Interlocked.Increment(ref this.Hits);
            return tokens;
        }

        _ = Interlocked.Increment(ref this.Misses);
        return null;
    }

    /// <summary>
    /// The sequence another simulation (or this one, before a clearing or an
    /// eviction) published for <paramref name="key"/>, asked after
    /// <see cref="TryGet"/> missed; <see langword="null"/> when none is kept.
    /// The parse replaying it calls <see cref="Adopt"/> where a live parse
    /// would have published.
    /// </summary>
    public Token[]? TryGetShared(in TokenMemoKey key)
    {
        if (!Shared.TryGetValue(key, out var tokens))
            return null;
        _ = Interlocked.Increment(ref this.SharedHits);
#if DEBUG
        if (Shared.AuditsThisHit())
            AuditShared(key, tokens);
#endif
        return tokens;
    }

    /// <summary>
    /// Stores a completed token sequence, here and in the process-wide tier.
    /// Only ever called with the tokens of a text the tokenizer walked to its
    /// end without raising, so a memo never holds a partial scan — which is
    /// what keeps a mid-text tokenization error (Msg 102 / 103 / 105 / 113)
    /// firing at exactly the character it fired at on the first execution —
    /// and never with a text that changes its own tokenizing inputs.
    /// </summary>
    public void Publish(in TokenMemoKey key, Token[] tokens)
    {
        _ = this.entries.TryAdd(key, tokens);
        _ = Shared.TryAdd(key, key.CommandText.Length, tokens);
    }

    /// <summary>
    /// Stores a sequence <see cref="TryGetShared"/> served, here only, at the
    /// point its parse would have published it had it tokenized live — so
    /// this memo's entries, admissions and evictions, and with them its
    /// <see cref="Hits"/> and <see cref="Misses"/>, are what they were before
    /// the tier.
    /// </summary>
    public void Adopt(in TokenMemoKey key, Token[] tokens) => _ = this.entries.TryAdd(key, tokens);

    /// <summary>
    /// Forgets every stored sequence — <c>DBCC FREEPROCCACHE</c> emptying the
    /// caches this one stands beside. Nothing can have gone stale, so this
    /// only costs the next executions their tokenizing; the process-wide tier
    /// stays, for the same reason.
    /// </summary>
    public void Clear() => this.entries.Clear();

#if DEBUG
    /// <summary>
    /// Tokenizes <paramref name="key"/>'s text again under its inputs and
    /// throws if the sequence the tier served differs in any token's kind,
    /// position, length, keyword, name or literal value.
    /// </summary>
    internal static void AuditShared(in TokenMemoKey key, Token[] shared)
    {
        var text = key.CommandText;
        var fresh = new List<Token>();
        var index = 0;
        while (Tokenizer.NextTokenPastBlanks(text, ref index, key.Collation, key.QuotedIdentifiers, key.CompatibilityLevel) is Token token)
        {
            if (token is not (Tokens.Whitespace or Tokens.Comment))
                fresh.Add(token);
        }
        if (fresh.Count != shared.Length)
            throw new InvalidOperationException($"The shared token tier served {shared.Length} tokens where tokenizing again gave {fresh.Count}, for: {text}");
        for (var i = 0; i < shared.Length; i++)
        {
            if (!SameToken(shared[i], fresh[i]))
                throw new InvalidOperationException($"The shared token tier's token {i} ('{shared[i].Source}' at {shared[i].StartIndex}) differs from tokenizing again ('{fresh[i].Source}' at {fresh[i].StartIndex}), for: {text}");
        }
    }

    private static bool SameToken(Token a, Token b) =>
        a.GetType() == b.GetType()
        && a.StartIndex == b.StartIndex
        && a.EndIndex == b.EndIndex
        && a.Source.SequenceEqual(b.Source)
        && (a, b) switch
        {
            (Tokens.ReservedKeyword x, Tokens.ReservedKeyword y) => x.Keyword == y.Keyword,
            (Tokens.Literal x, Tokens.Literal y) => SameValue(x.Value, y.Value),
            (Tokens.Numeric x, Tokens.Numeric y) => SameValue(x.Value, y.Value)
                && x.IntegerLiteralDigitCount == y.IntegerLiteralDigitCount && x.Underflowed == y.Underflowed && x.LexErrorNumber == y.LexErrorNumber,
            (Tokens.StringToken x, Tokens.StringToken y) => x.Span.SequenceEqual(y.Span),
            _ => true,
        };

    /// <summary>One literal's value against another's: the same type, collation and payload, exactly.</summary>
    private static bool SameValue(SqlValue a, SqlValue b) =>
        a.Type.GetType() == b.Type.GetType()
        && string.Equals(a.Type.ToString(), b.Type.ToString(), StringComparison.Ordinal)
        && ReferenceEquals(a.Type.Collation, b.Type.Collation)
        && a.IsNull == b.IsNull
        && (a.IsNull
            || a.Type.Category switch
            {
                SqlTypeCategory.String => string.Equals(a.AsString, b.AsString, StringComparison.Ordinal),
                _ when a.Type is VarbinarySqlType or BinarySqlType => a.AsBytes.AsSpan().SequenceEqual(b.AsBytes),
                _ => a.Equals(b),
            });
#endif
}

/// <summary>
/// Identity of a tokenization: every input <see cref="Tokenizer.NextToken"/>
/// reads. The command text and the <c>QUOTED_IDENTIFIER</c> setting decide
/// the token shapes; the collation tags string-literal
/// <see cref="SqlValue"/>s; the compatibility level decides which words are
/// reserved. Nothing else reaches the tokenizer, which is why an entry needs
/// no version stamp.
/// </summary>
internal readonly struct TokenMemoKey(string commandText, Collation collation, CompatibilityLevel compatibilityLevel, bool quotedIdentifiers)
    : IEquatable<TokenMemoKey>
{
    public readonly string CommandText = commandText;

    /// <summary>
    /// Compared by reference: a database holds one collation instance, and
    /// re-collating it installs a different one, so identity is the exact test
    /// wanted and costs no string compare.
    /// </summary>
    public readonly Collation Collation = collation;

    public readonly CompatibilityLevel CompatibilityLevel = compatibilityLevel;

    public readonly bool QuotedIdentifiers = quotedIdentifiers;

    // Implemented rather than inherited for the same reason PlanCacheKey
    // implements it: ValueType.Equals would box both sides and compare them
    // by reflection on every dictionary probe.
    public bool Equals(TokenMemoKey other) =>
        this.QuotedIdentifiers == other.QuotedIdentifiers
        && this.CompatibilityLevel == other.CompatibilityLevel
        && ReferenceEquals(this.Collation, other.Collation)
        && string.Equals(this.CommandText, other.CommandText, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TokenMemoKey other && this.Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(this.CommandText, this.Collation, this.CompatibilityLevel, this.QuotedIdentifiers);
}
