using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Vision;

// "Did this character stop moving for too long?" predicate. Stateless utility — the agent
// keeps the rolling history of (position, timestamp) samples and calls IsStuck after each
// new coordinate read.
//
// Logic: take all samples within [latest - StuckThreshold, latest]; if there exists a
// sample older than the threshold window (i.e. we've been tracking long enough) AND every
// sample within the window is within ToleranceMeters of the latest position, the character
// is considered stuck.
//
// History must be sorted chronologically ascending — caller's responsibility (typical: a
// ring buffer / list that the agent appends to). We don't sort defensively because
// per-agent histories are produced in order.
public sealed class StuckDetector
{
    private readonly int _toleranceMeters;
    private readonly TimeSpan _stuckThreshold;

    public StuckDetector(int toleranceMeters, TimeSpan stuckThreshold)
    {
        if (toleranceMeters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceMeters), "Tolerance must be non-negative.");
        }
        if (stuckThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stuckThreshold), "Stuck threshold must be positive.");
        }

        _toleranceMeters = toleranceMeters;
        _stuckThreshold = stuckThreshold;
    }

    public bool IsStuck(IReadOnlyList<(Coordinates Position, DateTimeOffset At)> history)
    {
        if (history.Count < 2)
        {
            return false;
        }

        var latest = history[^1];
        var windowStart = latest.At - _stuckThreshold;

        // Find the index of the first sample that's inside the threshold window.
        // Anything strictly older is needed only as evidence we've been tracking long
        // enough; we don't compare against those for distance.
        var windowStartIdx = 0;
        while (windowStartIdx < history.Count && history[windowStartIdx].At < windowStart)
        {
            windowStartIdx++;
        }

        // If no sample is older than the window edge, we haven't observed long enough to
        // claim "no movement for T seconds" — could be we just started tracking.
        if (windowStartIdx == 0)
        {
            return false;
        }

        // Every sample inside the window must be within tolerance of the latest position.
        // Bailing on the first far sample matches the spec's "не изменились за T секунд"
        // — any movement at all during the window means not stuck.
        for (var i = windowStartIdx; i < history.Count; i++)
        {
            if (latest.Position.HorizontalDistanceTo(history[i].Position) >= _toleranceMeters)
            {
                return false;
            }
        }
        return true;
    }
}
