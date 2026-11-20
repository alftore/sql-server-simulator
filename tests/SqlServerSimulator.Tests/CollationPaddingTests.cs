using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A string value's trailing spaces are padding to every operator that
/// compares it — <c>DISTINCT</c>, <c>GROUP BY</c>, a join's hash and
/// <c>ORDER BY</c> alike, under every collation body — while any other
/// character, a control below the space included, is not.
/// </summary>
[TestClass]
public class CollationPaddingTests
{
    private const string Rows = "(1,'a'),(2,'a  '),(3,'b'),(4,'a'+CHAR(31)),(5,'A')";

    private static (int Distinct, int Groups, int SelfJoin, string Order) Probe(string type, string collation, string rows = Rows)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (id int, v {type}(10) collate {collation}); insert t values {rows}");
        return (
            (int)sim.ExecuteScalar("select count(*) from (select distinct v from t) d")!,
            (int)sim.ExecuteScalar("select count(*) from (select v from t group by v) g")!,
            (int)sim.ExecuteScalar("select count(*) from t x join t y on x.v = y.v")!,
            (string)sim.ExecuteScalar("select string_agg(cast(id as varchar(10)), '') within group (order by v, id) from t")!);
    }

    /// <summary>
    /// Under the default collation <c>'a'</c>, <c>'a  '</c> and <c>'A'</c> are one
    /// group, and <c>'a' + CHAR(31)</c> is another: the control is not a space.
    /// It sorts below the space in a <c>varchar</c> (sort order 52 weighs it
    /// first) and above it in a Unicode column, ahead of <c>'b'</c> either way.
    /// </summary>
    [TestMethod]
    [DataRow("char", "41253")]
    [DataRow("varchar", "41253")]
    [DataRow("nchar", "12543")]
    [DataRow("nvarchar", "12543")]
    public void DefaultCollation_TrailingSpacesPad_ControlDoesNot(string type, string order)
    {
        var (distinct, groups, selfJoin, ordered) = Probe(type, "SQL_Latin1_General_CP1_CI_AS");
        AreEqual(3, distinct);
        AreEqual(3, groups);
        AreEqual(11, selfJoin);
        AreEqual(order, ordered);
    }
}
