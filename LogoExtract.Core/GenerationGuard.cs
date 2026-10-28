namespace LogoExtract.Core;

/// <summary>Owner's report, 2026-10-28: "không đồng nhất quản lý... khó theo dõi" -- before this, SegmentViewModel
/// tracked "has something newer superseded my in-flight work?" with a single private <c>int</c> field and
/// ItemsViewModel tracked the same concept with its own hand-rolled <c>Dictionary&lt;string,int&gt;</c> plus a
/// pair of local helper methods, two different shapes for the exact same rule. Both screens now share this one
/// class instead: <see cref="Bump"/> when the user starts something that should invalidate whatever's already
/// running for that key (region-select, a dashboard retry/override tap), <see cref="Current"/> to snapshot the
/// generation before starting an async call, and <see cref="IsCurrent"/> after it finishes to decide whether its
/// result is still wanted or must be discarded as stale. ONNX's InferenceSession.Run has no cancellation hook, so
/// this can never abort the in-flight call itself -- only let its eventual result recognize it's been superseded.
/// Thread-safe: results are checked back in from a background thread, not the UI thread.</summary>
public sealed class GenerationGuard<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, int> _generations = [];
    private readonly object _gate = new();

    /// <summary>The current generation for <paramref name="key"/> (0 if nothing has ever bumped it) -- call this
    /// right before starting an async operation, so its later <see cref="IsCurrent"/> check compares against the
    /// value as of when it started, not whatever it's drifted to by the time it finishes.</summary>
    public int Current(TKey key)
    {
        lock (_gate) return _generations.GetValueOrDefault(key);
    }

    /// <summary>Invalidates whatever is currently in flight for <paramref name="key"/>; returns the new
    /// generation (callers that also start new work right after bumping can reuse this return value instead of
    /// a separate <see cref="Current"/> call).</summary>
    public int Bump(TKey key)
    {
        lock (_gate)
        {
            int next = _generations.GetValueOrDefault(key) + 1;
            _generations[key] = next;
            return next;
        }
    }

    public bool IsCurrent(TKey key, int generation)
    {
        lock (_gate) return _generations.GetValueOrDefault(key) == generation;
    }
}
