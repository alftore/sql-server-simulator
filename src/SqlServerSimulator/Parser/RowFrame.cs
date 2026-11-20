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

    /// <summary>
    /// The frame the enclosing query's by-name resolver belongs to, when the
    /// resolver handed in is one: a subquery's references to its enclosing row
    /// are then read from that frame by reference, in place of a delegate call
    /// that looks the name up again.
    /// </summary>
    private readonly RowFrame? outerFrame = Of(outerResolver);

    private SourceColumnMemo? memo;
    private Binding[] bindings = NoBindings;

    /// <summary>
    /// A frame over one source's own row, which a computed column's expression
    /// reads through: its names bind by <see cref="Selection.FindWithinSource"/>
    /// and a read decodes the column as <see cref="Selection.DecodeOrCompute"/>
    /// does. It has no outer frame or resolver and never computes through
    /// another frame (Msg 1759 keeps a computed column off a computed one).
    /// </summary>
    private readonly bool withinSource;

    /// <summary>One within-source child frame per source, made on the first read of a computed column of it.</summary>
    private RowFrame?[]? computeFrames;

    private RowFrame(FromSource source, BatchContext batch) : this([source], batch, null)
    {
        this.withinSource = true;
        this.Tuple = new byte[1][];
    }

    /// <summary>The row tuple the frame reads; the site that owns the frame sets it per row.</summary>
    public byte[]?[] Tuple = null!;

    /// <summary>
    /// The by-name resolver over this frame, created on first request, for
    /// everything that is not a <see cref="Reference"/>.
    /// </summary>
    public Func<MultiPartName, SqlValue> Resolver => field ??= this.ResolveByName;

    /// <summary>
    /// The frame <paramref name="resolver"/> is the by-name resolver of, if it
    /// is one. Any other delegate — a probing run's wrapper that notes every
    /// name read, the grouped resolver, a cursor's — answers null and is called
    /// by name, so a wrapper is never skipped.
    /// </summary>
    public static RowFrame? Of(Func<MultiPartName, SqlValue>? resolver) =>
        resolver?.Target is RowFrame frame && ReferenceEquals(frame.Resolver, resolver) ? frame : null;

    private struct Binding
    {
        public Reference? Owner;
        public int Source;
        public int Column;

        /// <summary>The column is a non-persisted computed column the frame reads through a child frame.</summary>
        public bool Computed;
#if DEBUG
        public int Hits;
#endif
    }

    private SqlValue ResolveByName(MultiPartName name) =>
        this.withinSource
            ? Selection.ResolveWithinSource(sources[0], this.Tuple[0]!, batch, name)
            : Selection.ResolveAcrossTuple(sources, this.Tuple, name, batch, outerResolver, this.memo ??= new());

    /// <summary>The value of <paramref name="reference"/> in the current tuple.</summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public SqlValue Read(Reference reference)
    {
        var bindings = this.bindings;
        ref var binding = ref bindings[reference.Slot & (bindings.Length - 1)];
        if (!ReferenceEquals(binding.Owner, reference))
            return this.BindAndRead(reference);
#if DEBUG
        this.Audit(reference, ref binding);
#endif
        return this.ReadBound(reference, binding.Source, binding.Column, binding.Computed);
    }

    private SqlValue ReadBound(Reference reference, int source, int column, bool computed)
    {
        if (computed)
            return this.ComputeThroughFrame(source, column);
        if (this.withinSource)
            return Selection.DecodeOrCompute(sources[0], column, this.Tuple[0]!, batch);
        if (source >= 0)
            return Selection.ReadSourceColumn(sources, this.Tuple, source, column, batch, outerResolver, this.memo);
        if (source != -1)
            return RowLocator.Resolve(sources, this.Tuple, source, column, batch);
        if (this.outerFrame is { } outerFrame)
            return outerFrame.Read(reference);
        return outerResolver is { } outer
            ? outer(reference.ReferencedName)
            : throw Selection.UnresolvedNameError(sources, reference.ReferencedName);
    }

    /// <summary>
    /// A non-persisted computed column's value in the current row: its
    /// expression run over a within-source child frame whose bindings last
    /// the enumeration. Nothing but those bindings is kept, so a
    /// non-deterministic expression draws afresh on every read, and an error
    /// surfaces on the row and read that raises it today. The column's database
    /// is entered per read, as <c>Selection.Compute</c> does.
    /// </summary>
    private SqlValue ComputeThroughFrame(int source, int column)
    {
        var bytes = this.Tuple[source];
        var from = sources[source];
        if (bytes is null)
            return SqlValue.Null(from.Columns[column].Type);

        this.computeFrames ??= new RowFrame?[sources.Length];
        var child = this.computeFrames[source] ??= new RowFrame(from, batch);
        child.Tuple[0] = bytes;

        var scope = from.BackingTable?.OwningDatabase is { } owner ? ModuleDatabaseScope.Enter(batch.Connection, owner, bindsIdentity: false) : default;
        try
        {
            return from.Columns[column].Computed!.Run(new RuntimeContext(child, batch));
        }
        finally
        {
            scope.Exit();
        }
    }

    private SqlValue BindAndRead(Reference reference)
    {
        // Raises exactly what the by-name path raises (Msg 209, a graph
        // column's refusal) and stores nothing when it does.
        var name = reference.ReferencedName;
        var (source, column) = this.Resolve(name);
        var computed = !this.withinSource && source >= 0 && Selection.ReadsComputedExpression(sources[source], column);

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

        bindings[index] = new Binding { Owner = reference, Source = source, Column = column, Computed = computed };

        // A deferred column evaluates through the memo, which it shares with
        // the by-name path so the two resolve one name once.
        if (source >= 0 && sources[source].DeferredColumns is { } deferred && deferred[column] is not null)
            this.memo ??= new();

        return this.ReadBound(reference, source, column, computed);
    }

    private (int Source, int Column) Resolve(MultiPartName name)
    {
        if (!this.withinSource)
            return RowLocator.IsLocatorName(name) ? RowLocator.Find(sources, name) : Selection.FindSourceColumn(sources, name);

        // Inside a computed column's expression the name is matched on its
        // leaf against the source alone, and a miss is Msg 207.
        var index = Selection.FindWithinSource(sources[0], name.Leaf, batch.CurrentDatabase.Collation);
        return index >= 0 ? (0, index) : throw SimulatedSqlException.InvalidColumnName(name);
    }

#if DEBUG
    /// <summary>
    /// On a hit whose count is a power of two, resolves the name again and
    /// raises if the answer differs from the stored binding. The whole suite
    /// then tests for a stale binding on every query it runs, at a
    /// logarithmic number of extra resolutions, as <c>PlanCacheCaptureAudit</c>
    /// does for the shared-plan contract. Release builds carry none of it.
    /// </summary>
    private void Audit(Reference reference, ref Binding binding)
    {
        var hits = ++binding.Hits;
        if ((hits & (hits - 1)) != 0)
            return;
        var (source, column) = this.Resolve(reference.ReferencedName);
        if (source != binding.Source || column != binding.Column)
            throw new InvalidOperationException($"Column reference '{reference.ReferencedName}' is bound to ({binding.Source}, {binding.Column}) in its frame, but resolves to ({source}, {column}) after {hits} reads.");
    }
#endif
}
