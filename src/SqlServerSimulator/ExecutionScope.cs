namespace SqlServerSimulator;

/// <summary>
/// One command execution's cancellation scope. A <c>CommandTimeout</c> doesn't
/// give each scope a timer of its own: the scope records when it runs out
/// (<see cref="Deadline"/>) and the connection's one timer, due at the
/// earliest deadline in flight, cancels the scopes that have reached theirs
/// (<see cref="SimulatedDbConnection.FireDeadlines"/>).
/// </summary>
internal sealed class ExecutionScope : CancellationTokenSource
{
    /// <summary>
    /// When the scope's <c>CommandTimeout</c> runs out, as a
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp; 0 while none is
    /// armed, while it is paused (the client reading rows the statement sent)
    /// and once the scope has been disposed or has fired. Read and written with
    /// <see cref="Volatile"/> and claimed with a compare-exchange, so a pause
    /// and the timer's firing can't both have it.
    /// </summary>
    public long Deadline;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        Volatile.Write(ref this.Deadline, 0);
        base.Dispose(disposing);
    }
}
