using System.Runtime.CompilerServices;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// One enumerating method activation's column-resolution state: the sources
/// it reads, the current row tuple, and the binding of each
/// <see cref="Reference"/> it has met, found by the reference's own
/// <see cref="Reference.Slot"/> rather than by its name. It replaces, at each
/// site that builds one, the closure over the tuple and the
/// <see cref="SourceColumnMemo"/> the site would otherwise keep.
/// </summary>
/// <remarks>
/// <para>
/// A reference binds at its first <see cref="Read"/> in the frame, through the
/// two functions the memo calls on a miss, and stores nothing when they throw,
/// so every name error is the same exception at the same row and conjunct as
/// the by-name path raises it. A name no local source binds is stored as
/// unbound and takes the outer path on every read.
/// </para>
/// <para>
/// The binding lives here and never on the <see cref="Reference"/>: one node
/// is read at <c>(0, c)</c> by a scan prefilter and at <c>(s, c)</c> on the
/// joined tuple, and by every concurrent execution of a cached plan, which
/// shares the node and nothing else. A frame is used by one thread, as the
/// closure it replaces was. Bindings hold for the frame's life, which is what
/// the memo assumes: the <c>(source, column)</c> a name binds to over
/// <c>sources</c> does not change during the enumeration, so a frame is
/// constructed after the last assignment to the sources array its site reads.
/// </para>
/// </remarks>
internal sealed class RowFrame(FromSource[] sources, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
{
    private const int InitialSize = 4;
    private const int MaximumSize = 256;

    /// <summary>Shared by every frame until its first bind; never written.</summary>
    private static readonly Binding[] NoBindings = new Binding[1];

    private SourceColumnMemo? memo;
    private Binding[] bindings = NoBindings;

    /// <summary>The row tuple the frame reads; the site that owns the frame sets it per row.</summary>
    public byte[]?[] Tuple = null!;

    /// <summary>
    /// The by-name resolver over this frame, created on first request, for
    /// everything that is not a <see cref="Reference"/>.
    /// </summary>
    public Func<MultiPartName, SqlValue> Resolver => field ??= this.ResolveByName;

    private struct Binding
    {
        public Reference? Owner;
        public int Source;
        public int Column;
#if DEBUG
        public int Hits;
#endif
    }

    private SqlValue ResolveByName(MultiPartName name) =>
        Selection.ResolveAcrossTuple(sources, this.Tuple, name, batch, outerResolver, this.memo ??= new());

    /// <summary>The value of <paramref name="reference"/> in the current tuple.</summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public SqlValue Read(Reference reference)
    {
        var bindings = this.bindings;
        ref var binding = ref bindings[reference.Slot & (bindings.Length - 1)];
        if (!ReferenceEquals(binding.Owner, reference))
            return this.BindAndRead(reference);
#if DEBUG
        Audit(sources, reference, ref binding);
#endif
        return this.ReadBound(reference, binding.Source, binding.Column);
    }

    private SqlValue ReadBound(Reference reference, int source, int column)
    {
        if (source >= 0)
            return Selection.ReadSourceColumn(sources, this.Tuple, source, column, batch, outerResolver, this.memo);
        if (source != -1)
            return RowLocator.Resolve(sources, this.Tuple, source, column, batch);
        return outerResolver is { } outer
            ? outer(reference.ReferencedName)
            : throw Selection.UnresolvedNameError(sources, reference.ReferencedName);
    }

    private SqlValue BindAndRead(Reference reference)
    {
        // Raises exactly what the by-name path raises (Msg 209, a graph
        // column's refusal) and stores nothing when it does.
        var name = reference.ReferencedName;
        var (source, column) = Resolve(sources, name);

        var bindings = this.bindings;
        if (ReferenceEquals(bindings, NoBindings))
            this.bindings = bindings = new Binding[InitialSize];
        var index = reference.Slot & (bindings.Length - 1);
        if (bindings[index].Owner is not null)
        {
            // The slot holds another reference: double and re-insert, and at
            // the cap read this one by name, which is today's path and cost.
            if (bindings.Length == MaximumSize)
                return this.Resolver(name);
            var grown = new Binding[bindings.Length * 2];
            foreach (var kept in bindings)
            {
                if (kept.Owner is { } owner)
                    grown[owner.Slot & (grown.Length - 1)] = kept;
            }
            this.bindings = bindings = grown;
            index = reference.Slot & (bindings.Length - 1);
        }

        bindings[index] = new Binding { Owner = reference, Source = source, Column = column };

        // A deferred column evaluates through the memo, which it shares with
        // the by-name path so the two resolve one name once.
        if (source >= 0 && sources[source].DeferredColumns is { } deferred && deferred[column] is not null)
            this.memo ??= new();

        return this.ReadBound(reference, source, column);
    }

    private static (int Source, int Column) Resolve(FromSource[] sources, MultiPartName name) =>
        RowLocator.IsLocatorName(name) ? RowLocator.Find(sources, name) : Selection.FindSourceColumn(sources, name);

#if DEBUG
    /// <summary>
    /// On a hit whose count is a power of two, resolves the name again and
    /// raises if the answer differs from the stored binding. The whole suite
    /// then tests for a stale binding on every query it runs, at a
    /// logarithmic number of extra resolutions, as <c>PlanCacheCaptureAudit</c>
    /// does for the shared-plan contract. Release builds carry none of it.
    /// </summary>
    private static void Audit(FromSource[] sources, Reference reference, ref Binding binding)
    {
        var hits = ++binding.Hits;
        if ((hits & (hits - 1)) != 0)
            return;
        var (source, column) = Resolve(sources, reference.ReferencedName);
        if (source != binding.Source || column != binding.Column)
            throw new InvalidOperationException($"Column reference '{reference.ReferencedName}' is bound to ({binding.Source}, {binding.Column}) in its frame, but resolves to ({source}, {column}) after {hits} reads.");
    }
#endif
}
