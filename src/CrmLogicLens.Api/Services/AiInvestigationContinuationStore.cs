using System.Collections.Concurrent;
using System.Diagnostics;

namespace CrmLogicLens.Api.Services;

/// <summary>
/// Keeps an in-progress model/tool conversation on the server while the browser
/// performs an authorized CRM or live-form read. Only an opaque identifier leaves
/// the server; model reasoning state and tool messages remain process-local.
/// </summary>
public sealed class AiInvestigationContinuationStore(TimeProvider timeProvider)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(20);
    private readonly ConcurrentDictionary<Guid, AiInvestigationContinuation> _items = [];

    public Guid Save(Guid? id, AiInvestigationContinuation continuation)
    {
        RemoveExpired();
        var continuationId = id ?? Guid.NewGuid();
        _items[continuationId] = continuation with
        {
            ExpiresAt = timeProvider.GetUtcNow().Add(Retention)
        };
        return continuationId;
    }

    public bool TryTake(
        Guid id,
        Guid snapshotId,
        string question,
        out AiInvestigationContinuation continuation)
    {
        continuation = null!;
        if (!_items.TryRemove(id, out var stored) ||
            stored.ExpiresAt <= timeProvider.GetUtcNow() ||
            stored.SnapshotId != snapshotId ||
            !string.Equals(stored.Question, question, StringComparison.Ordinal))
        {
            return false;
        }
        continuation = stored;
        return true;
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in _items)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _items.TryRemove(item.Key, out _);
            }
        }
    }
}

public sealed record AiInvestigationContinuation(
    Guid SnapshotId,
    string Question,
    EvidenceToolSession Tools,
    List<object> Messages,
    Dictionary<string, string> CachedToolResults,
    int TotalToolCalls,
    int TotalToolCharacters,
    Stopwatch Stopwatch,
    DateTimeOffset InvestigationDeadline,
    DateTimeOffset ExpiresAt);
