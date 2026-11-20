using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The numeric constant conversion a comparison keeps on its node: what type the
/// pair compares in, and what the memo answers for a value it has seen, one it
/// has not, and one that does not convert. The memo is internal, so it is
/// reachable only here; the SQL-level behaviour is pinned beside the string memo's
/// in <c>ComparisonPromotionReuseTests</c>.
/// </summary>
[TestClass]
public sealed class ConstantConversionMemoTests
{
    private static readonly DecimalSqlType Decimal182 = DecimalSqlType.Get(18, 2);

    /// <summary>
    /// The comparison type of the pairs the memo's measurement rests on: a
    /// <c>decimal(18,2)</c> column against an <c>int</c> compares as the column's own
    /// type, so the literal's conversion is the whole conversion the pair costs.
    /// </summary>
    [TestMethod]
    public void ComparisonType_OfTheMeasuredPairs()
    {
        AreSame(Decimal182, BooleanExpression.ComparisonType(Decimal182, SqlType.Int32));
        AreSame(Decimal182, BooleanExpression.ComparisonType(SqlType.Int32, Decimal182));
        AreSame(SqlType.BigInt, BooleanExpression.ComparisonType(SqlType.Int32, SqlType.BigInt));
        AreEqual(DecimalSqlType.Get(11, 1), BooleanExpression.ComparisonType(SqlType.Int32, DecimalSqlType.Get(4, 1)));
        AreSame(SqlType.Int32, BooleanExpression.ComparisonType(SqlType.TinyInt, SqlType.Int32));
    }

    [TestMethod]
    public void RepeatedValue_IsConvertedOnceAndAnswersTheSame()
    {
        var memo = new ConstantConversionMemo();
        var source = SqlValue.FromInt32(50000);
        var first = memo.CoerceRight(source, Decimal182);
        for (var i = 0; i < 1000; i++)
        {
            var again = memo.CoerceRight(source, Decimal182);
            AreSame(Decimal182, again.Type);
            IsTrue(again.Equals(first));
        }

        IsTrue(first.Equals(source.CoerceTo(Decimal182)));
    }

    [TestMethod]
    public void ChangedValue_IsConvertedAgain()
    {
        var memo = new ConstantConversionMemo();
        AreEqual(50000m, memo.CoerceLeft(SqlValue.FromInt32(50000), Decimal182).AsDecimal);
        AreEqual(7m, memo.CoerceLeft(SqlValue.FromInt32(7), Decimal182).AsDecimal);
        AreEqual(50000m, memo.CoerceLeft(SqlValue.FromInt32(50000), Decimal182).AsDecimal);
        AreEqual(7m, memo.CoerceLeft(SqlValue.FromInt64(7), Decimal182).AsDecimal);
    }

    [TestMethod]
    public void ChangedTarget_IsConvertedAgain()
    {
        var memo = new ConstantConversionMemo();
        var source = SqlValue.FromInt32(5);
        AreSame(Decimal182, memo.CoerceRight(source, Decimal182).Type);
        AreSame(SqlType.BigInt, memo.CoerceRight(source, SqlType.BigInt).Type);
        AreSame(Decimal182, memo.CoerceRight(source, Decimal182).Type);
    }

    /// <summary>
    /// The key is the payload's identity, so negative zero and zero are two keys:
    /// each converts to its own result and never reads the other's.
    /// </summary>
    [TestMethod]
    public void NegativeZeroAndZero_AreNotFolded()
    {
        var memo = new ConstantConversionMemo();
        var negative = SqlValue.FromDouble(-0.0);
        var positive = SqlValue.FromDouble(0.0);
        IsFalse(negative.HasSamePayload(positive));
        for (var i = 0; i < 100; i++)
        {
            IsTrue(memo.CoerceLeft(positive, Decimal182).Equals(positive.CoerceTo(Decimal182)));
            IsTrue(memo.CoerceLeft(negative, Decimal182).Equals(negative.CoerceTo(Decimal182)));
        }
    }

    /// <summary>
    /// Two scales of one number are two payloads: the type is part of the key.
    /// </summary>
    [TestMethod]
    public void TwoScalesOfOneNumber_AreNotFolded()
    {
        var memo = new ConstantConversionMemo();
        var narrow = SqlValue.FromDecimal(DecimalSqlType.Get(5, 1), 1.5m);
        var wide = SqlValue.FromDecimal(DecimalSqlType.Get(9, 4), 1.5m);
        for (var i = 0; i < 100; i++)
        {
            AreSame(SqlType.Float, memo.CoerceRight(narrow, SqlType.Float).Type);
            AreEqual(1.5, memo.CoerceRight(narrow, SqlType.Float).AsDouble);
            AreEqual(1.5, memo.CoerceRight(wide, SqlType.Float).AsDouble);
        }
    }

    /// <summary>
    /// A conversion that fails stores nothing: the same value raises again, from the
    /// same call, and a good value after it converts normally.
    /// </summary>
    [TestMethod]
    public void FailedConversion_IsNeverStored()
    {
        var memo = new ConstantConversionMemo();
        var huge = SqlValue.FromInt64(9_000_000_000_000_000_000L);
        for (var i = 0; i < 4; i++)
            _ = Throws<SimulatedSqlException>(() => memo.CoerceRight(huge, SqlType.SmallMoney));

        IsTrue(memo.CoerceRight(SqlValue.FromInt64(5), SqlType.SmallMoney).Equals(SqlValue.FromInt64(5).CoerceTo(SqlType.SmallMoney)));
        for (var i = 0; i < 4; i++)
            _ = Throws<SimulatedSqlException>(() => memo.CoerceRight(huge, SqlType.SmallMoney));
    }

    [TestMethod]
    public void Null_AndNonNumeric_PassThroughUnremembered()
    {
        var memo = new ConstantConversionMemo();
        var nullValue = memo.CoerceLeft(SqlValue.Null(SqlType.Int32), Decimal182);
        IsTrue(nullValue.IsNull);
        AreSame(Decimal182, nullValue.Type);
        AreEqual(5m, memo.CoerceLeft(SqlValue.FromVarchar("5"), Decimal182).AsDecimal);
    }
}
