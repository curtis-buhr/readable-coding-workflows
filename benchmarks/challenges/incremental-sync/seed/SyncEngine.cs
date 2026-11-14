namespace IncrementalSync;

public sealed record SourceRecord(long Sequence, string Payload);

public interface IRecordSink
{
    ValueTask<bool> WriteAsync(string tenantId, SourceRecord record, CancellationToken cancellationToken);
}

/// <summary>Copies a source batch to its destination in source order.</summary>
public sealed class SyncEngine(IRecordSink sink)
{
    /// <summary>Writes records until the destination rejects a write.</summary>
    /// <returns>The number of accepted writes.</returns>
    public async ValueTask<int> SyncAsync(string tenantId, IReadOnlyList<SourceRecord> records,
        CancellationToken cancellationToken = default)
    {
        ValidateBatch(tenantId, records);
        cancellationToken.ThrowIfCancellationRequested();
        var acceptedCount = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await sink.WriteAsync(tenantId, record, cancellationToken))
                break;
            acceptedCount++;
        }
        return acceptedCount;
    }

    /// <summary>Checks that the batch is safe to process before any writes occur.</summary>
    private static void ValidateBatch(string tenantId, IReadOnlyList<SourceRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(records);

        // A strictly increasing sequence lets readers stop or resume without sorting the batch.
        long previousSequence = 0;
        foreach (var record in records)
        {
            if (record is null || record.Sequence <= previousSequence || record.Payload is null)
                throw new ArgumentException("Records must have increasing positive sequences and non-null payloads.",
                    nameof(records));
            previousSequence = record.Sequence;
        }
    }
}
