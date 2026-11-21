namespace SqlServerSimulator;

/// <summary>
/// <see cref="string.GetHashCode()"/> of a command text, computed once per text
/// and thread, for the keys that fold a command text into a hash. A first
/// execution of a command asks for it from a dozen places — its token memo and
/// the process-wide tier under it, the plan-cache key it builds for the batch
/// and the walk's remembered compile, the plan registry's lookup and add, the
/// statement ends — and each is a pass over the whole text, which measured at
/// about 5 ns per character of command text across a first execution
/// (docs/claude/plan-cache.md, "A first execution").
/// <para>
/// The value is exactly the string's own hash, so a key hashing through
/// <see cref="Of"/> hashes as it did hashing the text itself, and dictionary
/// order and every collision are what they were. The memo is the last two texts
/// asked, found by reference: a text is immutable, so a reference that matches
/// has the hash it had. Only a text long enough for the pass to matter and
/// short enough to be worth holding is remembered (the process-wide token tier's
/// bound), so a thread keeps at most two such strings alive.
/// </para>
/// </summary>
internal static class TextHash
{
    /// <summary>The shortest text remembered; a shorter hashes in less than the memo's lookup costs.</summary>
    internal const int MinLength = 64;

    [ThreadStatic]
    private static string? recent;

    [ThreadStatic]
    private static int recentHash;

    [ThreadStatic]
    private static string? older;

    [ThreadStatic]
    private static int olderHash;

#if DEBUG
    private static int audits;
#endif

    /// <summary>The hash <paramref name="text"/> gives itself, taken from the memo when it was asked for lately; 0 for none, as a key's <see cref="HashCode.Combine{T1}(T1)"/> reads it.</summary>
    public static int Of(string? text)
    {
        if (text is null)
            return 0;
        if (text.Length is < MinLength or > Parser.TokenMemo.MaxSharedTextLength)
            return text.GetHashCode(StringComparison.Ordinal);
        if (ReferenceEquals(text, recent))
            return Audited(text, recentHash);
        if (ReferenceEquals(text, older))
        {
            // Promoted, so a pair of texts asked in turn keeps both.
            var hash = olderHash;
            older = recent;
            olderHash = recentHash;
            recent = text;
            recentHash = hash;
            return Audited(text, hash);
        }
        var computed = text.GetHashCode(StringComparison.Ordinal);
        older = recent;
        olderHash = recentHash;
        recent = text;
        recentHash = computed;
        return computed;
    }

    private static int Audited(string text, int remembered)
    {
#if DEBUG
        if (System.Numerics.BitOperations.IsPow2(Interlocked.Increment(ref audits)) && remembered != text.GetHashCode(StringComparison.Ordinal))
            throw new InvalidOperationException($"TextHash remembered {remembered} for a text whose hash is {text.GetHashCode(StringComparison.Ordinal)}: {text}");
#endif
        return remembered;
    }

    /// <summary>Ordinal equality over texts, hashing through <see cref="Of"/>: the comparer of a dictionary keyed by a command text.</summary>
    internal sealed class Comparer : IEqualityComparer<string>
    {
        public static readonly Comparer Instance = new();

        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public int GetHashCode(string obj) => Of(obj);
    }
}
