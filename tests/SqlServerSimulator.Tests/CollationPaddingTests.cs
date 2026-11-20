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

    /// <summary>
    /// A binary collation keeps case and weighs the control as a character of
    /// its own, so <c>'a'</c>, <c>'A'</c> and <c>'a' + CHAR(31)</c> stay apart
    /// and only <c>'a  '</c> pads into <c>'a'</c>. <c>_BIN2</c> and every
    /// <c>varchar</c> compare as if the shorter operand were padded, which puts
    /// the control below the space; the pre-BIN2 <c>nvarchar</c> form does not
    /// pad, so there the shorter <c>'a'</c> sorts first.
    /// </summary>
    [TestMethod]
    [DataRow("char", "Latin1_General_BIN", "54123")]
    [DataRow("varchar", "Latin1_General_BIN", "54123")]
    [DataRow("nchar", "Latin1_General_BIN", "51243")]
    [DataRow("nvarchar", "Latin1_General_BIN", "51243")]
    [DataRow("char", "Latin1_General_BIN2", "54123")]
    [DataRow("varchar", "Latin1_General_BIN2", "54123")]
    [DataRow("nchar", "Latin1_General_BIN2", "54123")]
    [DataRow("nvarchar", "Latin1_General_BIN2", "54123")]
    [DataRow("char", "Latin1_General_100_BIN2_UTF8", "54123")]
    [DataRow("varchar", "Latin1_General_100_BIN2_UTF8", "54123")]
    [DataRow("nchar", "Latin1_General_100_BIN2_UTF8", "54123")]
    [DataRow("nvarchar", "Latin1_General_100_BIN2_UTF8", "54123")]
    public void BinaryCollation_TrailingSpacesPad_CaseAndControlDoNot(string type, string collation, string order)
    {
        var (distinct, groups, selfJoin, ordered) = Probe(type, collation);
        AreEqual(4, distinct);
        AreEqual(4, groups);
        AreEqual(7, selfJoin);
        AreEqual(order, ordered);
    }

    /// <summary>
    /// Windows names weigh a trailing space away the same way: <c>'a'</c>,
    /// <c>'a  '</c> and <c>'A'</c> are one group, <c>'b'</c> another. The
    /// control row is left out, since how these names weigh it is unprobed.
    /// </summary>
    [TestMethod]
    [DataRow("char", "French_CI_AS")]
    [DataRow("varchar", "French_CI_AS")]
    [DataRow("nchar", "French_CI_AS")]
    [DataRow("nvarchar", "French_CI_AS")]
    [DataRow("char", "Japanese_CI_AS")]
    [DataRow("varchar", "Japanese_CI_AS")]
    [DataRow("nchar", "Japanese_CI_AS")]
    [DataRow("nvarchar", "Japanese_CI_AS")]
    public void WindowsCollation_TrailingSpacesPad(string type, string collation)
    {
        var (distinct, groups, selfJoin, ordered) = Probe(type, collation, "(1,'a'),(2,'a  '),(3,'b'),(5,'A')");
        AreEqual(2, distinct);
        AreEqual(2, groups);
        AreEqual(10, selfJoin);
        AreEqual("1253", ordered);
    }
}
