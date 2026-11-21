using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="TextHash"/> answers a text's own hash, whatever it was asked
/// lately, from any thread.
/// </summary>
[TestClass]
public sealed class TextHashTests
{
    [TestMethod]
    public void Of_IsTheStringsOwnHash()
    {
        AreEqual(0, TextHash.Of(null));
        foreach (var length in new[] { 0, 1, TextHash.MinLength - 1, TextHash.MinLength, 500, Parser.TokenMemo.MaxSharedTextLength, Parser.TokenMemo.MaxSharedTextLength + 1 })
        {
            var text = new string('q', length);
            AreEqual(text.GetHashCode(), TextHash.Of(text));
            AreEqual(text.GetHashCode(), TextHash.Of(text));
            AreEqual(text.GetHashCode(), TextHash.Of(new string(text.AsSpan())));
        }
    }

    [TestMethod]
    public void Of_RemembersByInstance_AndNeverAnswersAnotherTextsHash()
    {
        var one = new string('a', 100);
        var two = new string('b', 100);
        var three = new string('c', 100);
        for (var i = 0; i < 20; i++)
        {
            foreach (var text in new[] { one, two, one, three, two, three, one })
                AreEqual(text.GetHashCode(), TextHash.Of(text));
        }
    }

    [TestMethod]
    public void Of_AnswersEachThreadItsOwn()
    {
        var texts = Enumerable.Range(0, 6).Select(i => new string((char)('a' + i), 200)).ToArray();
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 50_000; i++)
            {
                var text = texts[random.Next(texts.Length)];
                if (TextHash.Of(text) != text.GetHashCode())
                    return false;
            }
            return true;
        }, TestContext.CancellationToken)).ToArray();
        Task.WaitAll(workers, TestContext.CancellationToken);
        IsTrue(workers.All(worker => worker.Result));
    }

    public TestContext TestContext { get; set; } = null!;
}
