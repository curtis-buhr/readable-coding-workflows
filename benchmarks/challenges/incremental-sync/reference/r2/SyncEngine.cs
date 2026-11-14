namespace IncrementalSync;

public sealed record SourceRecord(long Sequence, string Payload);

public interface IRecordSink
{
    ValueTask<bool> WriteAsync(string tenantId, SourceRecord record, CancellationToken cancellationToken);
}

public interface ICheckpointStore
{
    long Read(string tenantId);
    void Save(string tenantId, long sequence);
}

public sealed record TenantBatch(string TenantId, IReadOnlyList<SourceRecord> Records);
public sealed record TenantProgress(string TenantId, int AcknowledgedCount, bool Blocked);
public sealed class RetryableSinkException(string message) : Exception(message);

/// <summary>Copies each tenant's ordered source records and persists acknowledged progress.</summary>
public sealed class SyncEngine
{
    private readonly IRecordSink _sink;
    private readonly ICheckpointStore _checkpoints;

    /// <summary>Creates an engine with checkpoints scoped to this instance.</summary>
    public SyncEngine(IRecordSink sink) : this(sink, new MemoryCheckpoints()) { }

    /// <summary>Creates an engine that resumes using the supplied checkpoint store.</summary>
    public SyncEngine(IRecordSink sink, ICheckpointStore checkpoints)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(checkpoints);
        _sink = sink;
        _checkpoints = checkpoints;
    }

    /// <summary>Copies uncheckpointed records for one tenant until the sink declines a write.</summary>
    /// <returns>The number of acknowledgements saved during this call.</returns>
    public async ValueTask<int> SyncAsync(string tenantId, IReadOnlyList<SourceRecord> records,
        CancellationToken cancellationToken = default)
    {
        ValidateBatch(tenantId, records);
        var progress = await CopyAsync(tenantId, records, allowRetryableFailure: false, cancellationToken);
        return progress.AcknowledgedCount;
    }

    /// <summary>Copies tenants in input order, continuing after a tenant's retryable sink failure.</summary>
    public async ValueTask<IReadOnlyList<TenantProgress>> SyncAllAsync(IReadOnlyList<TenantBatch> batches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var tenants = new HashSet<string>(StringComparer.Ordinal);

        // Reject a malformed later tenant before earlier tenants create any durable effects.
        foreach (var batch in batches)
        {
            if (batch is null)
                throw new ArgumentException("A tenant batch must exist.", nameof(batches));
            ValidateBatch(batch.TenantId, batch.Records);
            if (!tenants.Add(batch.TenantId))
                throw new ArgumentException("A tenant can appear only once.", nameof(batches));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<TenantProgress>(batches.Count);
        foreach (var batch in batches)
        {
            var progress = await CopyAsync(batch.TenantId, batch.Records, allowRetryableFailure: true,
                cancellationToken);
            results.Add(new TenantProgress(batch.TenantId, progress.AcknowledgedCount, progress.Blocked));
        }
        return results;
    }

    /// <summary>Writes one tenant and saves each acknowledgement before sending the next record.</summary>
    private async ValueTask<(int AcknowledgedCount, bool Blocked)> CopyAsync(string tenantId,
        IReadOnlyList<SourceRecord> records, bool allowRetryableFailure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = _checkpoints.Read(tenantId);
        var acknowledgedCount = 0;
        foreach (var record in records)
        {
            if (record.Sequence <= checkpoint)
                continue;
            cancellationToken.ThrowIfCancellationRequested();

            // Catch failures only at the sink boundary. A checkpoint-store error stops the call.
            bool acknowledged;
            try
            {
                acknowledged = await _sink.WriteAsync(tenantId, record, cancellationToken);
            }
            catch (RetryableSinkException)
            {
                if (!allowRetryableFailure)
                    throw;
                return (acknowledgedCount, true);
            }

            if (!acknowledged)
                return (acknowledgedCount, true);

            _checkpoints.Save(tenantId, record.Sequence);
            acknowledgedCount++;
        }
        return (acknowledgedCount, false);
    }

    /// <summary>Checks tenant identity and the complete source batch before it can be written.</summary>
    private static void ValidateBatch(string tenantId, IReadOnlyList<SourceRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(records);
        long previousSequence = 0;
        foreach (var record in records)
        {
            if (record is null || record.Sequence <= previousSequence || record.Payload is null)
                throw new ArgumentException("Records must have increasing positive sequences and non-null payloads.",
                    nameof(records));
            previousSequence = record.Sequence;
        }
    }

    /// <summary>Stores checkpoints for the compatibility constructor for the life of its engine.</summary>
    private sealed class MemoryCheckpoints : ICheckpointStore
    {
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);

        /// <summary>Returns the latest saved sequence, or zero for a new tenant.</summary>
        public long Read(string tenantId) => _sequences.GetValueOrDefault(tenantId);

        /// <summary>Records an acknowledged sequence.</summary>
        public void Save(string tenantId, long sequence) => _sequences[tenantId] = sequence;
    }
}
