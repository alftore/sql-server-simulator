using System.Runtime.CompilerServices;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-enumeration memo for <c>Selection.FindSourceColumn</c>. The name →
/// (source, column) binding is fixed for a given sources array, but the
/// per-row resolvers re-derive it once per column reference per row —
/// profiled at ~45% of total CPU on a four-way-join aggregate report over
/// 230k rows, the dominant cost of every scan-bound join / aggregate /
/// window query. Lookup is a short linear scan comparing the name's
/// component <b>string references</b>: a parsed <c>Reference</c> node passes
/// the same <see cref="MultiPartName"/> (hence the same string instances)
/// every row, so reference identity is a stable, near-free key. A miss falls
/// through to one full resolution and appends (copy-on-write, so an
/// accidentally-shared memo degrades to extra appends rather than torn
/// reads).
/// </summary>
/// <remarks>
/// One instance is created per enumerating method activation, beside the
/// closure that captures the sources array — execution-scoped, so a
/// plan-cached <c>Selection</c>'s concurrent executions never share one
/// (the shared-plan contract in <c>plan-cache.md</c>). The entry list stops
/// growing at <see cref="CapacityCap"/> — distinct reference names per query
/// are few, so the cap only guards against a hypothetical caller minting
/// fresh name strings per row, which would otherwise grow the memo without
/// ever hitting it.
/// <para>
/// Readers take no lock. They read the published count first, then the array
/// reference, and scan only indexes below that count. A slot is written
/// exactly once, before the count that covers it is published
/// (<see cref="Volatile.Write{T}(ref T, T)"/> — release), and never again, so
/// the multi-word entry at any index below a count a reader saw is
/// completely written and immutable; a slot at or above the count is never
/// read, so a half-written one is never observed. Growth builds the larger
/// array completely (copy plus the new entry), publishes the array, then the
/// count, so the array a reader loads after the count always holds valid
/// entries for every index below it (arrays only ever grow, and a newer array
/// carries identical entries). One appender writes at a time: a second,
/// which only an accidentally-shared memo could bring, drops its entry and
/// resolves that name again later, so two writers never share a slot.
/// </para>
/// </remarks>
internal sealed class SourceColumnMemo
{
    private const int CapacityCap = 64;

    private const int InitialCapacity = 2;

    private (string Leaf, string? Qualifier, int SourceIndex, int ColumnIndex)[] entries = [];

    private int count;

    private int appending;

    /// <summary>
    /// Memoized <c>Selection.FindSourceColumn</c>: the (source, column)
    /// location of <paramref name="name"/> across <paramref name="sources"/>,
    /// or <c>(-1, -1)</c> for the outer-scope fallthrough; a
    /// <see cref="RowLocator"/> name binds as <see cref="RowLocator.Find"/>
    /// says, with a source below <c>-1</c>. Msg 209 ambiguity
    /// still raises from the underlying resolution (never cached — it throws
    /// before the append).
    /// </summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public (int SourceIndex, int ColumnIndex) Find(FromSource[] sources, MultiPartName name)
    {
        var seen = Volatile.Read(ref this.count);
        var snapshot = Volatile.Read(ref this.entries);
        for (var i = 0; i < seen; i++)
        {
            var (leaf, qualifier, memoSource, memoColumn) = snapshot[i];
            if (ReferenceEquals(leaf, name.Leaf) && ReferenceEquals(qualifier, name.ImmediateQualifier))
                return (memoSource, memoColumn);
        }

        var (sourceIndex, columnIndex) = RowLocator.IsLocatorName(name)
            ? RowLocator.Find(sources, name)
            : Selection.FindSourceColumn(sources, name);
        if (seen < CapacityCap)
            this.Append((name.Leaf, name.ImmediateQualifier, sourceIndex, columnIndex));

        return (sourceIndex, columnIndex);
    }

    private void Append((string Leaf, string? Qualifier, int SourceIndex, int ColumnIndex) entry)
    {
        if (Interlocked.CompareExchange(ref this.appending, 1, 0) != 0)
            return;
        try
        {
            var used = this.count;
            if (used >= CapacityCap)
                return;

            var current = this.entries;
            if (used < current.Length)
            {
                current[used] = entry;
            }
            else
            {
                var grown = new (string, string?, int, int)[Math.Min(Math.Max(current.Length * 2, InitialCapacity), CapacityCap)];
                Array.Copy(current, grown, used);
                grown[used] = entry;
                Volatile.Write(ref this.entries, grown);
            }

            Volatile.Write(ref this.count, used + 1);
        }
        finally
        {
            Volatile.Write(ref this.appending, 0);
        }
    }
}
