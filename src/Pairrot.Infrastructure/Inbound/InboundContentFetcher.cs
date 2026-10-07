using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Transactions;

namespace Pairrot.Infrastructure.Inbound;

/// <summary>
/// Retrieves deferred content through the content source of the instance the message is bound to,
/// never through the current default provider. The snapshot, the message state, and the job's
/// completion are saved together, and only while this worker still holds the job's lease.
/// </summary>
public class InboundContentFetcher(
    PairrotDbContext db,
    BackgroundJobQueue jobs,
    InboundRouter router,
    InboundProviderRegistry providers,
    IEnumerable<IInboundContentSource> sources,
    ILogger<InboundContentFetcher> logger) : IBackgroundJobHandler
{
    public string Type => BackgroundJobTypes.FetchInboundContent;

    /// <summary>Only where some configured instance has a content source; otherwise jobs wait for a process that has one.</summary>
    public bool IsEnabled => providers.All.Any(i => sources.Any(s => s.Kind == i.Kind));

    public async Task HandleAsync(JobLease lease, CancellationToken ct)
    {
        var message = await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == lease.TargetId, ct);
        if (message is null)
        {
            await FinishAsync(lease, null, (job, _) => jobs.Fail(job, "message_not_found", "The inbound message does not exist."), ct);
            return;
        }

        if (message.ContentState == InboundContentState.Available)
        {
            // Already stored (e.g. by an earlier attempt that committed); never store a second snapshot.
            await FinishAsync(lease, null, (job, _) => jobs.Succeed(job), ct);
            return;
        }

        if (providers.Find(message.ProviderInstanceId) is not { } instance)
        {
            await FailAsync(lease, "provider_not_configured",
                $"Provider instance '{message.ProviderInstanceId}' is not configured on this server.", ct);
            return;
        }

        if (message.ContentMode != InboundContentMode.Deferred || message.ContentReference is not { } reference
            || sources.FirstOrDefault(s => s.Kind == instance.Kind) is not { } source)
        {
            await FailAsync(lease, "content_fetch_not_supported", "This message's source cannot retrieve content.", ct);
            return;
        }

        var result = await source.RetrieveAsync(instance, reference, ct);
        switch (result.Outcome)
        {
            case ContentRetrievalOutcome.Retrieved when result.Snapshot is { } snapshot:
                await StoreAsync(lease, message, snapshot, ct);
                break;
            case ContentRetrievalOutcome.NotYetAvailable:
                // A brand-new email may not be retrievable yet; only exhausted retries make it unavailable.
                await RetryOrFailAsync(lease, "content_unavailable", result.Error ?? "The content is not available.", null, ct);
                break;
            case ContentRetrievalOutcome.TemporaryFailure:
                await RetryOrFailAsync(lease, "provider_unavailable", result.Error ?? "The provider is temporarily unavailable.",
                    result.RetryAfter, ct);
                break;
            case ContentRetrievalOutcome.Unavailable:
                await FailAsync(lease, "content_unavailable", result.Error ?? "The content is no longer available.", ct);
                break;
            case ContentRetrievalOutcome.ConfigurationError:
                await FailAsync(lease, "provider_unauthorized", result.Error ?? "The provider rejected the configured credentials.", ct);
                break;
            case ContentRetrievalOutcome.TooLarge:
                await FailAsync(lease, "content_too_large", result.Error ?? "The content exceeds the size limit.", ct);
                break;
            default:
                await FailAsync(lease, "invalid_provider_response", result.Error ?? "The provider response is not the requested email.", ct);
                break;
        }
    }

    private async Task StoreAsync(JobLease lease, InboundMessage original, InboundContentSnapshot snapshot, CancellationToken ct)
    {
        if (InboundContent.Validate(snapshot) is { } invalid)
        {
            await FailAsync(lease, "invalid_provider_response", invalid, ct);
            return;
        }

        if (snapshot.Size > providers.MaxContentBytes)
        {
            await FailAsync(lease, "content_too_large",
                $"The content exceeds Pairrot's limit of {providers.MaxContentBytes} bytes and was not stored.", ct);
            return;
        }

        var address = original.InboundAddressId is { } addressId
            ? await db.InboundAddresses.AsNoTracking().SingleAsync(a => a.Id == addressId, ct)
            : null;
        var mismatch = address is not null && await router.ContradictsRouteAsync(snapshot, address, ct);

        await FinishAsync(lease, original.Id, (job, message) =>
        {
            InboundContent.Store(message!, snapshot, jobs.Now());
            if (mismatch)
            {
                // Conflicting evidence never moves the message to another owner; it is held instead.
                message!.HoldReason = InboundHoldReason.RecipientMismatch;
            }

            ParseScheduling.AddFirstJob(jobs, message!);

            jobs.Succeed(job);
        }, ct);
    }

    private Task RetryOrFailAsync(JobLease lease, string code, string error, TimeSpan? retryAfter, CancellationToken ct)
    {
        if (jobs.Schedule.NextAttempt(lease.AttemptCount, jobs.Now(), retryAfter) is not { } next)
        {
            return FailAsync(lease, code, $"{error} No attempts left.", ct);
        }

        return FinishAsync(lease, lease.TargetId, (job, message) =>
        {
            InboundContent.RecordError(message!, code, error, jobs.Now());
            BackgroundJobQueue.Retry(job, next, code, error);
        }, ct);
    }

    private Task FailAsync(JobLease lease, string code, string error, CancellationToken ct) =>
        FinishAsync(lease, lease.TargetId, (job, message) =>
        {
            InboundContent.Fail(message!, code, error, jobs.Now());
            jobs.Fail(job, code, error);
        }, ct);

    private async Task FinishAsync(JobLease lease, Guid? messageId, Action<BackgroundJob, InboundMessage?> apply, CancellationToken ct)
    {
        var saved = await jobs.FinishAsync(lease, async job =>
        {
            var message = messageId is { } id ? await db.InboundMessages.SingleOrDefaultAsync(m => m.Id == id, ct) : null;
            if (messageId is not null && message is null)
            {
                jobs.Fail(job, "message_not_found", "The inbound message does not exist.");
                return;
            }

            apply(job, message);
        }, ct);

        if (!saved)
        {
            logger.LogWarning("Job {JobId} lost its lease before finishing; its result was discarded.", lease.JobId);
        }
    }
}
