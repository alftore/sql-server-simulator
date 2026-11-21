using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="Simulation.PlanCacheKey"/> is a dictionary key that computes its
/// hash as it is built, so a key must hash and compare by its fields' values,
/// never by the instances its strings happen to be.
/// </summary>
[TestClass]
public sealed class PlanCacheKeyTests
{
    private static Simulation.PlanCacheKey Key(
        string text = "select 1",
        string database = "master",
        string schema = "dbo",
        string signature = "@p int",
        bool quotedIdentifiers = true,
        DateOrder? dateFormat = null,
        bool ansiNulls = true,
        bool concatNullYieldsNull = true) =>
        new(new string(text), new string(database), new string(schema), new string(signature), quotedIdentifiers, dateFormat ?? DateOrder.Mdy, ansiNulls, concatNullYieldsNull);

    [TestMethod]
    public void EqualKeysFromDifferentStringInstances_HashAndCompareEqual()
    {
        var first = Key();
        var second = Key();

        IsFalse(ReferenceEquals(first.CommandText, second.CommandText));
        IsTrue(first.Equals(second));
        IsTrue(first.Equals((object)second));
        AreEqual(first.GetHashCode(), second.GetHashCode());

        var map = new Dictionary<Simulation.PlanCacheKey, int> { [first] = 7 };
        AreEqual(7, map[second]);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("database")]
    [DataRow("schema")]
    [DataRow("signature")]
    [DataRow("quoted")]
    [DataRow("dateformat")]
    [DataRow("ansinulls")]
    [DataRow("concat")]
    public void KeysDifferingInOneField_AreNotEqual(string field)
    {
        var changed = field switch
        {
            "text" => Key(text: "select 2"),
            "database" => Key(database: "tempdb"),
            "schema" => Key(schema: "other"),
            "signature" => Key(signature: "@p bigint"),
            "quoted" => Key(quotedIdentifiers: false),
            "dateformat" => Key(dateFormat: DateOrder.Dmy),
            "ansinulls" => Key(ansiNulls: false),
            "concat" => Key(concatNullYieldsNull: false),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var baseline = Key();
        IsFalse(baseline.Equals(changed));
        IsFalse(changed.Equals(baseline));

        var map = new Dictionary<Simulation.PlanCacheKey, int> { [baseline] = 1 };
        IsFalse(map.ContainsKey(changed));
    }
}
