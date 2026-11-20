namespace SqlServerSimulator.Storage;

/// <summary>
/// A memo over <see cref="SqlValue.CoerceTo"/> for a <em>numeric</em> source
/// crossing into another numeric type, held by the comparison node whose
/// operand slot is a constant. One entry per side, so one object per node.
/// </summary>
/// <remarks>
/// <para>
/// <c>WHERE Amount &gt; 50000</c> beside a <c>decimal(18,2)</c> column promotes
/// to the decimal type, so the literal's <c>int</c> was converted once per
/// scanned row, to the same answer every time. The memo keeps the last
/// conversion of each side, as <see cref="StringCoercionMemo"/> does for a string.
/// </para>
/// <para>
/// The key is the source's type and the target, both by reference, and the
/// source's payload by <em>identity</em>: the 64-bit primitive and the reference
/// field (<see cref="SqlValue.HasSamePayload"/>). A numeric payload is a
/// primitive or an immutable boxed <c>Decimal38</c>, so an identical payload
/// under an identical type pair is the same call to a function of nothing else
/// — <c>CoerceTo</c>'s numeric branches read no batch, connection or session
/// setting. Value equality is avoided on purpose: it could fold <c>-0</c> onto
/// <c>0</c>, or two scales of one number onto each other. A parameter's wide
/// decimal is one box per execution, so two executions with equal values miss
/// once, and a concurrent replay with another value cannot read this one's entry.
/// </para>
/// <para>
/// The caller hands it constant operands only: a column side changes on every
/// row and would allocate an entry per row for nothing. A failing conversion
/// (Msg 8115, 232) is never memoized, so the error surfaces from the row that
/// raised it, as often as it did.
/// </para>
/// <para>
/// A cached plan is shared across sessions, so each entry is immutable and
/// published whole with <see cref="Volatile"/>; two threads racing to fill it
/// compute the same value and one wins, so no lock is taken. Debug builds
/// convert again on every power-of-two hit and throw if the memo's answer
/// differs, as <c>RowFrame</c> does for a column binding.
/// </para>
/// </remarks>
internal sealed class ConstantConversionMemo
{
    private Entry? leftEntry, rightEntry;

#if DEBUG
    private int leftHits, rightHits;
#endif

    /// <summary>Converts the left operand of the comparison.</summary>
    public SqlValue CoerceLeft(SqlValue value, SqlType target) =>
#if DEBUG
        Coerce(ref this.leftEntry, ref this.leftHits, value, target);
#else
        Coerce(ref this.leftEntry, value, target);
#endif

    /// <summary>Converts the right operand of the comparison.</summary>
    public SqlValue CoerceRight(SqlValue value, SqlType target) =>
#if DEBUG
        Coerce(ref this.rightEntry, ref this.rightHits, value, target);
#else
        Coerce(ref this.rightEntry, value, target);
#endif

#if DEBUG
    private static SqlValue Coerce(ref Entry? slot, ref int hits, SqlValue value, SqlType target)
#else
    private static SqlValue Coerce(ref Entry? slot, SqlValue value, SqlType target)
#endif
    {
        if (value.IsNull || !IsNumeric(value.Type.Category) || !IsNumeric(target.Category) || ReferenceEquals(value.Type, target))
            return value.CoerceTo(target);

        var current = Volatile.Read(ref slot);
        if (current is not null
            && ReferenceEquals(current.Target, target)
            && value.HasSamePayload(current.Source))
        {
#if DEBUG
            Audit(current, value, target, Interlocked.Increment(ref hits));
#endif
            return current.Result;
        }

        var coerced = value.CoerceTo(target);
        Volatile.Write(ref slot, new Entry(value, target, coerced));
        return coerced;
    }

    private static bool IsNumeric(SqlTypeCategory category) =>
        category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate;

#if DEBUG
    private static void Audit(Entry entry, SqlValue value, SqlType target, int count)
    {
        if ((count & (count - 1)) != 0)
            return;
        var fresh = value.CoerceTo(target);
        if (!ReferenceEquals(fresh.Type, entry.Result.Type) || !fresh.Equals(entry.Result))
            throw new InvalidOperationException($"The memoized conversion of a {value.Type} constant to {target} is {entry.Result.DebugDisplay()}, but converting it again gives {fresh.DebugDisplay()} after {count} hits.");
    }
#endif

    private sealed class Entry(SqlValue source, SqlType target, SqlValue result)
    {
        public readonly SqlValue Source = source;
        public readonly SqlType Target = target;
        public readonly SqlValue Result = result;
    }
}
