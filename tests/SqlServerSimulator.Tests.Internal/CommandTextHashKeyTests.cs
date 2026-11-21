using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The keys that fold a command text into their hash, and the cache keyed by a
/// batch's text, answer by the text's content and not its instance, and hash
/// as the fields they hold combine: what a text's hash being taken once per
/// text and not once per key relies on.
/// </summary>
[TestClass]
public sealed class CommandTextHashKeyTests
{
    private static string LongText(char tag) => "/* " + new string(tag, 200) + " */ SELECT 1 AS a, 2 AS b -- " + new string(tag, 80);

    [TestMethod]
    public void EachKeyHashesAsItsFieldsCombine_ForShortAndLongTexts()
    {
        foreach (var text in new[] { "SELECT 1", LongText('a'), LongText('b'), "", new string('x', 40_000) })
        {
            var copy = new string(text.AsSpan());
            var collation = Collation.Baseline;
            var memoKey = new TokenMemoKey(copy, collation, CompatibilityLevel.Sql170, true);
            AreEqual(HashCode.Combine(text, collation, CompatibilityLevel.Sql170, true), memoKey.GetHashCode());

            var planKey = new Simulation.PlanCacheKey(copy, "db", "dbo", "", true, DateOrder.Mdy, true, true);
            AreEqual(HashCode.Combine(text, "db", "dbo", "", true, DateOrder.Mdy, true, true), planKey.GetHashCode());

            var cachedKey = new CachedPlanKey(CachedPlanKind.Adhoc, 5, 0, copy, null, 7, 1033, 1, 7);
            AreEqual(HashCode.Combine(CachedPlanKind.Adhoc, (short)5, 0, text, (string?)null, 7, (short)1, (byte)7), cachedKey.GetHashCode());
            IsTrue(cachedKey.Equals(new CachedPlanKey(CachedPlanKind.Adhoc, 5, 0, text, null, 7, 1033, 1, 7)));
        }
    }

    [TestMethod]
    public void AKeysHash_DoesNotChangeBetweenInterleavedTexts()
    {
        var texts = new[] { LongText('a'), LongText('b'), LongText('c') };
        var expected = texts.Select(text => HashCode.Combine(text, Collation.Baseline, CompatibilityLevel.Sql170, false)).ToArray();
        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < texts.Length; i++)
            {
                var index = ((i * 2) + round) % texts.Length;
                AreEqual(expected[index], new TokenMemoKey(texts[index], Collation.Baseline, CompatibilityLevel.Sql170, false).GetHashCode());
            }
        }
    }

    [TestMethod]
    public void TheStatementEndsOfABatch_AreFoundByAnEqualTextOfAnotherInstance()
    {
        // A statement carrying a secret is pinned by its text, which masks it wherever the text is reported.
        var text = "CREATE LOGIN [hash_key_login] WITH PASSWORD = N'Str0ng!Passw0rd', CHECK_POLICY = OFF; SELECT 1 AS padding_padding_padding_padding";
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = text;
            _ = command.ExecuteNonQuery();
        }
        var other = new string(text.AsSpan());
        IsFalse(ReferenceEquals(text, other));
        IsTrue(simulation.HidesBatchText(other));
        IsTrue(simulation.SecretsKnown(other));
        IsNotNull(simulation.MaskedBatchText(other));
        IsFalse(simulation.HidesBatchText(other + " "));
    }
}
