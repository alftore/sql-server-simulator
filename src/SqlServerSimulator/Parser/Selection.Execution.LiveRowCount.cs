using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

internal sealed partial class Selection
{
    /// <summary>
    /// The row count a query's one implicit group is answered with, for a query
    /// whose every aggregate is a plain <c>COUNT(*)</c> / <c>COUNT_BIG(*)</c>
    /// over one whole user table: the heap's live rows
    /// (<see cref="Heap.CountLiveRows()"/>), which are the rows its scan yields,
    /// counted without reading any of them. Null for any other shape, and
    /// whenever the scan could observe more than the slots hold: a lock another
    /// session's write holds on the table (the scan waits it out row by row), a
    /// snapshot read, a row-level security filter, a row another request of the
    /// transaction wrote, a statement reading row addresses or already holding a
    /// scan's position, or a write landing while the slots are counted — each
    /// leaves the scan to answer.
    /// <para>
    /// The read leaves what the scan it replaces leaves: one scan of the base
    /// index charged to the index usage counters (as <see cref="FromSource.RowsFor"/>
    /// charges it), and, while the statement counts its reads (<c>STATISTICS IO</c>,
    /// Query Store, the plan cache's statistics), a scan of the table that
    /// enters every page holding a row — which is the scan's reads only for a
    /// heap order, so a table the scan reads in its clustered key's order
    /// (<see cref="ClusteredScan.Order"/>) keeps the scan then. Both are
    /// charged only once the count stands, so a decline leaves them to the scan.
    /// </para>
    /// </summary>
    private static long? TryCountLiveRows(
        FromSource[] sources, JoinSpec[] joins, FromClause fromClause, List<AggregateExpression> aggregates, BatchContext batch)
    {
        if (sources.Length != 1 || joins.Length != 0 || aggregates.Count == 0 || fromClause.Excluders.Count != 0 || fromClause.Having is not null
            || sources[0] is not
            {
                BackingTable: { OwningDatabase: not null, IsMemoryOptimized: false, IsTableVariable: false } table,
                LateralPlan: null,
                ForPath: false,
                ForcedAccessPath: null,
                Seeks: false,
                SeekOrdinals: null,
                Rows: LockCheckedScanRows or UnlockedScanRows,
                HeapPlan: { RowMode: null, SerializableRangeMode: null, Fence: null, Pages: null, WriteTargetAddresses: null, SkipBlockedRows: false, SnapshotConflictCheck: false, RowTxScoped: false } plan,
            } source
            || batch.CurrentStatement is not { RowAddresses: null, ProbedTable: null })
        {
            return null;
        }
        foreach (var aggregate in aggregates)
        {
            if (aggregate.Kind is not (AggregateKind.Count or AggregateKind.CountBig) || aggregate.Distinct || (aggregate.Operand is not null && !aggregate.CountsRowsOnly))
                return null;
        }
        if (RowSecurity.For(batch, table) is not null || batch.OwnWritesOf(table) is not null
            || (!plan.NoLockReader && batch.ResolveSnapshotXidForRead(table, plan) is not null))
        {
            return null;
        }

        // The sequence is read before the lock state: a writer's lock is granted
        // before its write lands, so a write already visible in the sequence has
        // its lock visible below, and one landing after changes the sequence.
        var heap = table.Heap;
        var sequence = heap.WriteSequence;
        var io = batch.Connection.StatementIo;
        if ((sequence & 1) != 0 || (io is not null && ClusteredScan.Order(table, out _) is not null)
            || Volatile.Read(ref table.ActiveDataWriters) != 0 || Volatile.Read(ref table.ActivePageLocks) != 0
            || Volatile.Read(ref table.ActiveKeyRangeLocks) != 0 || !table.SupersededKeyImages.IsEmptyLockFree())
        {
            return null;
        }
        var count = heap.CountLiveRows(out var pages);
        if (heap.WriteSequence != sequence)
            return null;
        FromSource.NoteRead(table, source.SeekOrdinals, source.Seeks, source.SeekIsEquality, batch);
        if (io?.Touch(table) is { } counts)
        {
            counts.ScanCount++;
            counts.LogicalReads += pages;
        }
        return count;
    }
}
