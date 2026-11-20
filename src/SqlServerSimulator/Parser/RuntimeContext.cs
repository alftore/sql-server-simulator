using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Runtime context for one <see cref="Expression.Run(RuntimeContext)"/> call: the column
/// resolver that maps multi-part names to values for the current row, plus
/// the <see cref="BatchContext"/> the expression is executing in. Bundled
/// into a single parameter so most expressions reach for
/// <see cref="ResolveColumn"/> and ignore <see cref="Batch"/>, while late-
/// bound expressions (<see cref="Expressions.CurrentTimeFunction"/>,
/// <see cref="Expressions.LastIdentityExpression"/>,
/// <see cref="Expressions.RowCountExpression"/>,
/// <see cref="Expressions.IdentCurrent"/>) read per-statement / per-session /
/// per-database state through <see cref="Batch"/> at evaluation time —
/// correct even when the expression was parsed in a different batch (e.g. a
/// column default's <c>getutcdate()</c> parsed at CREATE TABLE and run on
/// every later INSERT).
/// </summary>
/// <remarks>
/// The struct is two words: one reference field holding either the by-name
/// resolver or a <see cref="RowFrame"/>, and the batch. A site that enumerates
/// rows builds a frame instead of a closure, and a
/// <see cref="Expressions.Reference"/> reads its column through it by
/// reference rather than by name; everything else asks for
/// <see cref="ResolveColumn"/> and gets the frame's by-name resolver.
/// </remarks>
internal readonly struct RuntimeContext
{
    /// <summary>The by-name resolver, or the <see cref="RowFrame"/> that owns one.</summary>
    private readonly object? columns;

    /// <summary>The batch this expression is executing in.</summary>
    public readonly BatchContext Batch;

    public RuntimeContext(Func<MultiPartName, SqlValue> resolveColumn, BatchContext batch)
    {
        this.columns = resolveColumn;
        this.Batch = batch;
    }

    public RuntimeContext(RowFrame frame, BatchContext batch)
    {
        this.columns = frame;
        this.Batch = batch;
    }

    /// <summary>Maps a (possibly qualified) column name to its value for the current row.</summary>
    public Func<MultiPartName, SqlValue> ResolveColumn =>
        this.columns as Func<MultiPartName, SqlValue> ?? (this.columns as RowFrame)?.Resolver!;

    /// <summary>The frame a <see cref="Expressions.Reference"/> reads through, if this runtime has one.</summary>
    public RowFrame? Frame => this.columns as RowFrame;
}
