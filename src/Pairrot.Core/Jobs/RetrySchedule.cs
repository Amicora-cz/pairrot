namespace Pairrot.Core.Jobs;

/// <summary>Bounded retries: a fixed number of attempts with configured delays between them.</summary>
/// <param name="MaxAttempts">Total attempts, the first one included.</param>
/// <param name="Delays">The wait after attempt 1, 2, ...; the last delay repeats if there are fewer delays than retries.</param>
public sealed record RetrySchedule(int MaxAttempts, IReadOnlyList<TimeSpan> Delays)
{
    /// <summary>
    /// When to try again after <paramref name="attemptsMade"/> failed attempts, or <c>null</c> when
    /// no attempt is left. A provider's <c>Retry-After</c> is respected when it asks for a longer
    /// wait, but never beyond the longest configured delay.
    /// </summary>
    public DateTimeOffset? NextAttempt(int attemptsMade, DateTimeOffset now, TimeSpan? retryAfter = null)
    {
        if (attemptsMade >= MaxAttempts || Delays.Count == 0)
        {
            return null;
        }

        var delay = Delays[Math.Clamp(attemptsMade, 1, Delays.Count) - 1];
        if (retryAfter is { } requested && requested > delay)
        {
            delay = requested < Delays.Max() ? requested : Delays.Max();
        }

        return now + delay;
    }
}
