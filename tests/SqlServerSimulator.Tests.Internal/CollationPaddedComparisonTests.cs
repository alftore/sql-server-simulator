using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Pins <see cref="Collation.ComparePadded"/>, <see cref="Collation.EqualsPadded"/>
/// and <see cref="Collation.GetPaddedHashCode"/> to the semantics they replace: the
/// collation's own comparison of both operands with their trailing U+0020 trimmed,
/// and a hash that agrees with that equality, on every comparer body.
/// </summary>
[TestClass]
public sealed class CollationPaddedComparisonTests
{
    private static readonly string[] Names =
    [
        "SQL_Latin1_General_CP1_CI_AS",
        "SQL_Latin1_General_CP1_CS_AS",
        "SQL_Latin1_General_Pref_CP1_CI_AS",
        "Latin1_General_CI_AS",
        "Latin1_General_100_CS_AS",
        "Latin1_General_100_CI_AI",
        "SQL_Latin1_General_CP1250_CI_AS",
        "French_CI_AS",
        "Japanese_CI_AS",
        "Chinese_PRC_CI_AS",
        "Latin1_General_BIN",
        "Latin1_General_BIN2",
        "Latin1_General_100_BIN2_UTF8",
        "Japanese_BIN2",
    ];

    // Letters and a case pair, a precomposed and a decomposed accent, spaces (listed
    // thrice so trailing runs and inner gaps are common), the symbols a tiebreak
    // weighs, NUL, tab and a control, a ligature and its spelling, other spaces
    // that do not pad (no-break, ideographic), a lone combining mark, Greek, an
    // ideograph, half- and full-width katakana, a surrogate pair, two format
    // characters, Thai with a tone mark and a fullwidth letter.
    private static readonly string[] Alphabet =
    [
        "a", "A", "b", "\u00E1", "e\u0301", " ", " ", " ", "-", "'", "\0", "\t", "\u0001",
        "\u00E6", "\u00DF", "ss", "\u00A0", "\u3000", "\u0301", "\u03B1", "\u6F22", "\u30A2",
        "\uFF71", "\uD83D\uDE00", "\uFEFF", "\u200B", "\u0E01", "\u0E48", "\uFF33",
    ];

    private static readonly string[] Fixed =
    [
        "", " ", "a", "a ", "a  ", "A ", "a\0", "a\0 ", "a\t", "a-", "a- ", "co-op ", "coop",
        "\u00E6 ", "ae", "\u00DF  ", "ss", "a\u00A0 ", "a\u3000",
    ];

    private static IEnumerable<(Collation Collation, bool Varchar)> Bodies()
    {
        foreach (var name in Names)
        {
            var collation = Collation.Get(name);
            yield return (collation, false);
            var varchar = collation.ForVarcharStorage();
            if (!ReferenceEquals(varchar, collation))
                yield return (varchar, true);
        }
    }

    private static string Escape(string s) =>
        string.Concat(s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    private static bool RoundTrips(Collation varchar, string s) =>
        varchar.StorageEncoding.GetString(varchar.StorageEncoding.GetBytes(s)) == s;

    [TestMethod]
    public void PaddedForms_MatchTrimmedComparison_AndHashAgrees()
    {
        var random = new Random(20261120);
        var failures = new List<string>();
        foreach (var (collation, varchar) in Bodies())
        {
            var label = $"{collation.Name}/{collation.GetType().Name}/{(varchar ? "varchar" : "nvarchar")}";
            // A varchar body only ever sees what its code page holds.
            var alphabet = varchar ? Array.FindAll(Alphabet, a => RoundTrips(collation, a)) : Alphabet;
            var samples = new List<string>();
            for (var i = 0; i < 400; i++)
            {
                var length = random.Next(0, 5);
                var text = string.Concat(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]));
                samples.Add(text + new string(' ', random.Next(0, 4)));
            }
            samples.AddRange(varchar ? Array.FindAll(Fixed, a => RoundTrips(collation, a)) : Fixed);
            foreach (var x in samples)
            {
                foreach (var y in samples)
                {
                    var tx = x.TrimEnd(' ');
                    var ty = y.TrimEnd(' ');
                    var expectedOrder = Math.Sign(collation.Compare(tx, ty));
                    var actualOrder = Math.Sign(collation.ComparePadded(x, y));
                    if (expectedOrder != actualOrder)
                        failures.Add($"{label} compare '{Escape(x)}' vs '{Escape(y)}': {actualOrder}, trimmed {expectedOrder}");
                    var expectedEqual = collation.Equals(tx, ty);
                    var actualEqual = collation.EqualsPadded(x, y);
                    if (expectedEqual != actualEqual)
                        failures.Add($"{label} equals '{Escape(x)}' vs '{Escape(y)}': {actualEqual}, trimmed {expectedEqual}");
                    if (actualEqual && collation.GetPaddedHashCode(x) != collation.GetPaddedHashCode(y))
                        failures.Add($"{label} hash '{Escape(x)}' vs '{Escape(y)}'");
                }
            }
        }
        var summary = failures.GroupBy(f => f[..f.IndexOf('\'', StringComparison.Ordinal)]).Select(g => $"{g.Count(),6} {g.Key} e.g. {string.Join(" | ", g.Take(3))}");
        IsEmpty(failures, string.Join("\n", summary));
    }
}
