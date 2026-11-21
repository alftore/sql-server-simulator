#if DEBUG
using System.Numerics;
#endif
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Multiply : TwoSidedExpression
{
    internal Multiply(Expression left, Expression right) : base(left, right) { }

    protected override SqlValue Run(SqlValue left, SqlValue right)
    {
        // int * int, as long and narrowed, with the overflow Msg 8115 PureIntegerArithmetic raises for the same pair.
        if (ReferenceEquals(left.Type, SqlType.Int32) && ReferenceEquals(right.Type, SqlType.Int32) && !left.IsNull && !right.IsNull)
        {
            var result = (long)left.AsInt32 * right.AsInt32;
            var fast = result is >= int.MinValue and <= int.MaxValue ? SqlValue.FromInt32((int)result) : throw SimulatedSqlException.ArithmeticOverflow("int");
#if DEBUG
            if (BitOperations.IsPow2(Interlocked.Increment(ref fastPathHits)))
            {
                var general = RunGeneral(left, right);
                if (general.Type != fast.Type || general.IsNull || general.AsInt32 != fast.AsInt32)
                    throw new InvalidOperationException($"The int * int fast path answered {fast.AsInt32} where the general path answered {(general.IsNull ? "NULL" : general.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");
            }
#endif
            return fast;
        }
        return RunGeneral(left, right);
    }

#if DEBUG
    private static long fastPathHits;
#endif

    private static SqlValue RunGeneral(SqlValue left, SqlValue right) => IntegerArithmetic(left, right, '*', static (a, b) => checked(a * b));

    protected override char Operator => '*';
}
