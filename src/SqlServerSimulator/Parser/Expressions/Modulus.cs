#if DEBUG
using System.Numerics;
#endif
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Modulus : TwoSidedExpression
{
    internal Modulus(Expression left, Expression right) : base(left, right) { }

    protected override SqlValue Run(SqlValue left, SqlValue right)
    {
        // int % int: the remainder takes the dividend's sign as C# does, Msg 8134 for a zero divisor and Msg 8115 for int.MinValue % -1, as PureIntegerArithmetic raises them.
        if (ReferenceEquals(left.Type, SqlType.Int32) && ReferenceEquals(right.Type, SqlType.Int32) && !left.IsNull && !right.IsNull)
        {
            var divisor = right.AsInt32;
            if (divisor == 0)
                throw SimulatedSqlException.DivideByZero();
            // int.MinValue % -1 overflows for both operators: the quotient is formed for % too.
            var fast = divisor == -1 && left.AsInt32 == int.MinValue ? throw SimulatedSqlException.ArithmeticOverflow("int") : SqlValue.FromInt32(left.AsInt32 % divisor);
#if DEBUG
            if (BitOperations.IsPow2(Interlocked.Increment(ref fastPathHits)))
            {
                var general = RunGeneral(left, right);
                if (general.Type != fast.Type || general.IsNull || general.AsInt32 != fast.AsInt32)
                    throw new InvalidOperationException($"The int % int fast path answered {fast.AsInt32} where the general path answered {(general.IsNull ? "NULL" : general.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");
            }
#endif
            return fast;
        }
        return RunGeneral(left, right);
    }

#if DEBUG
    private static long fastPathHits;
#endif

    private static SqlValue RunGeneral(SqlValue left, SqlValue right) => IntegerArithmetic(left, right, '%', static (a, b) => a % b);

    protected override char Operator => '%';
}
