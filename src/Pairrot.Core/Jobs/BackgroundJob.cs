namespace Pairrot.Core.Jobs;

/// <summary>
/// Durable work stored in PostgreSQL. A worker claims a job with an expiring lease and must
/// still hold that lease when it saves the result, so an interrupted job is resumed and a stale
/// worker cannot overwrite a newer completion. At most one job per type and target is active;
/// running an operation again after it ended starts the next generation.
/// </summary>
public class BackgroundJob
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public Guid TargetId { get; set; }
    public int Generation { get; set; } = 1;
    public BackgroundJobState State { get; set; } = BackgroundJobState.Queued;

    /// <summary>Attempts started so far, counted when the job is claimed.</summary>
    public int AttemptCount { get; set; }

    public DateTimeOffset DueAt { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>A short error safe to show to users and operators; never a secret or a provider response body.</summary>
    public string? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }

    /// <summary>Who queued the job: <c>system</c>, a user, an API key, or an operator.</summary>
    public required string RequestedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum BackgroundJobState
{
    Queued,
    Running,
    RetryWait,
    Succeeded,
    Failed,
}

public static class BackgroundJobTypes
{
    /// <summary>Retrieve an inbound message's content from the provider; the target is the message.</summary>
    public const string FetchInboundContent = "fetch_inbound_content";

    /// <summary>List a provider instance's received messages to recover missed deliveries; the target is its sync state.</summary>
    public const string DiscoverInboundMessages = "discover_inbound_messages";

    /// <summary>Parse a stored message into an incoming transaction; the target is the message.</summary>
    public const string ParseInboundMessage = "parse_inbound_message";

    /// <summary>Match an incoming transaction to its payment request (Feature 009); the target is the transaction.</summary>
    public const string MatchTransaction = "match_transaction";

    /// <summary>Match the waiting transactions of a newly created payment request; the target is the request.</summary>
    public const string MatchPaymentRequest = "match_payment_request";
}
