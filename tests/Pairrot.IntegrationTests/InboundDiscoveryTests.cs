using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 006 criteria 12-14: the startup and hourly recovery check lists the provider's received
/// messages and recovers missed webhooks through common intake.
/// </summary>
[Collection(PairrotCollection.Name)]
public class InboundDiscoveryTests(PairrotFixture fx)
{
    private InboundHarness Inbound => fx.Inbound;

    private DateTimeOffset Now => Inbound.Clock.GetUtcNow();

    [Fact] // AC 12
    public async Task Startup_check_recovers_a_missed_webhook_through_the_common_intake_path()
    {
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));
        var box = await Inbound.CreateMailboxAsync();
        var emailId = InboundHarness.NewEmailId();
        Inbound.Api.List(emailId, Now.AddMinutes(-10), InboundHarness.Listed(emailId, Now.AddMinutes(-10), box.Address));
        Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address])));
        await Inbound.SetSyncAsync(Now.AddDays(-1), null);

        // A process with the worker schedules the check itself at startup.
        await using (var withWorker = Inbound.CreateFactory(worker: true))
        {
            withWorker.CreateClient();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            string? state = null;
            while (state != "available" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                var list = await fx.Client(box.Key, withWorker).GetAsync("/v1/tenant/inbound-messages").ExpectAsync(HttpStatusCode.OK);
                state = list.Items().FirstOrDefault()?.Str("content_state");
            }

            Assert.Equal("available", state);
        }

        var sync = await Inbound.SyncStateAsync();
        Assert.NotNull(sync.LastSuccessAt);
        Assert.NotNull(sync.CoveredThrough);
        Assert.True(sync.LastRunDiscoveredCount >= 1);
        Assert.NotNull(sync.NextDueAt);
        await fx.WithDbAsync(async db =>
        {
            var receipt = await db.InboundDeliveryReceipts.SingleAsync(r => r.SourceDeliveryId == $"discovery:{emailId}");
            Assert.Equal(("resend_list_api", "provider_api_access"), (receipt.TransportKind, receipt.DeliveryEvidence));
        });

        // A late webhook adds its receipt to the same message; a repeated scan changes nothing and fetches nothing again.
        await Inbound.PostEventAsync(InboundHarness.ReceivedEvent(emailId, box.Address)).ExpectAsync(HttpStatusCode.OK);
        await Inbound.DiscoverAsync();
        Assert.Equal(0, (await Inbound.SyncStateAsync()).LastRunDiscoveredCount);
        Assert.Equal(1, Inbound.Api.CallsFor(emailId));
        await fx.WithDbAsync(async db =>
        {
            var message = await db.InboundMessages.SingleAsync(m => m.SourceMessageId == emailId);
            Assert.Equal(2, await db.InboundDeliveryReceipts.CountAsync(r => r.InboundMessageId == message.Id));
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == message.Id));
        });
    }

    [Fact] // AC 12
    public async Task Checks_are_hourly_and_never_overlap()
    {
        using var scope = Inbound.Factory.Services.CreateScope();
        var discovery = scope.ServiceProvider.GetRequiredService<InboundDiscoveryService>();
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));

        // Startup always schedules; a second scheduling round while the check is queued adds nothing.
        Assert.True(await discovery.ScheduleAsync(startup: true) >= 1);
        Assert.Equal(0, await discovery.ScheduleAsync(startup: true));
        await Inbound.RunJobsAsync();

        // Hourly: nothing before the next due time, then one check per discoverable instance.
        Assert.Equal(0, await discovery.ScheduleAsync(startup: false));
        Inbound.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(discovery.Discoverable.Count, await discovery.ScheduleAsync(startup: false));
        await Inbound.RunJobsAsync();

        // Instances whose adapter cannot list messages get no check and no API call.
        Assert.Equal([InboundHarness.SecondInstanceId, InboundHarness.InstanceId], discovery.Discoverable.Select(i => i.Id).Order());
        await fx.WithDbAsync(async db => Assert.False(await db.InboundSyncStates.AnyAsync(s =>
            s.ProviderInstanceId == "stub" || s.ProviderInstanceId == InboundHarness.DeferredInstanceId
            || s.ProviderInstanceId == InboundHarness.InlineInstanceId || s.ProviderInstanceId == InboundHarness.RemoteInstanceId)));
        Assert.All(Inbound.Api.ListRequests, r => Assert.Contains(r.Host, new[] { "resend.fake", "resend-b.fake" }));
    }

    [Fact] // AC 12
    public async Task A_concurrent_webhook_and_check_create_one_message_and_one_retrieval()
    {
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));
        var box = await Inbound.CreateMailboxAsync();
        var emailId = InboundHarness.NewEmailId();
        Inbound.Api.List(emailId, Now, InboundHarness.Listed(emailId, Now, box.Address));
        await Inbound.SetSyncAsync(Now.AddHours(-2), null);

        await Task.WhenAll(
            Inbound.PostEventAsync(InboundHarness.ReceivedEvent(emailId, box.Address)).ExpectAsync(HttpStatusCode.OK),
            Inbound.DiscoverAsync());

        await fx.WithDbAsync(async db =>
        {
            var message = await db.InboundMessages.SingleAsync(m => m.SourceMessageId == emailId);
            Assert.Equal(2, await db.InboundDeliveryReceipts.CountAsync(r => r.InboundMessageId == message.Id));
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == message.Id));
        });
    }

    [Fact] // AC 13
    public async Task Multi_page_scans_after_a_long_outage_survive_failures_between_pages()
    {
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));
        var box = await Inbound.CreateMailboxAsync();
        var covered = Microseconds(Now.AddDays(-3));
        await Inbound.SetSyncAsync(Now.AddDays(-10), covered);

        // Four messages in the unscanned range (one inside the overlap before the boundary), one long covered.
        var inRange = new[] { covered.AddMinutes(-30), Now.AddDays(-2), Now.AddDays(-1), Now.AddMinutes(-1) }
            .Select(at => (Id: InboundHarness.NewEmailId(), At: at)).ToList();
        var covered5Days = (Id: InboundHarness.NewEmailId(), At: Now.AddDays(-5));
        foreach (var (id, at) in inRange.Append(covered5Days))
        {
            Inbound.Api.List(id, at, InboundHarness.Listed(id, at, box.Address));
        }

        // The provider fails once on a later page.
        var failed = 0;
        Inbound.Api.ListOverride = (host, after) => host == "resend.fake" && after is not null && Interlocked.CompareExchange(ref failed, 1, 0) == 0
            ? FakeResendApi.Status(HttpStatusCode.ServiceUnavailable)()
            : null;
        try
        {
            await Inbound.DiscoverAsync();
            var incomplete = await Inbound.SyncStateAsync();
            Assert.Equal(covered, incomplete.CoveredThrough);
            Assert.Equal("discovery_incomplete", incomplete.LastErrorCode);
            var job = await LatestDiscoveryJobAsync();
            Assert.Equal(BackgroundJobState.RetryWait, job.State);

            // After a restart, the retry repeats the same range and completes it.
            Inbound.Clock.Advance(TimeSpan.FromMinutes(2));
            await using var restarted = Inbound.CreateFactory();
            await Inbound.RunJobsAsync(restarted);
        }
        finally
        {
            Inbound.Api.ListOverride = null;
        }

        var complete = await Inbound.SyncStateAsync();
        Assert.True(complete.CoveredThrough > covered);
        Assert.Null(complete.LastErrorCode);
        await fx.WithDbAsync(async db =>
        {
            foreach (var (id, _) in inRange)
            {
                Assert.Equal(1, await db.InboundMessages.CountAsync(m => m.SourceMessageId == id));
            }

            Assert.False(await db.InboundMessages.AnyAsync(m => m.SourceMessageId == covered5Days.Id));
        });
        Assert.True(Inbound.Api.ListRequests.Count(r => r.Host == "resend.fake" && r.After is not null) >= 2);
    }

    [Fact] // AC 13
    public async Task A_check_whose_lease_expired_cannot_advance_the_boundary()
    {
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));
        await Inbound.SetSyncAsync(Now.AddDays(-1), null);
        using (var scope = Inbound.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<InboundDiscoveryService>().RunNowAsync(InboundHarness.InstanceId, "test");
        }

        using var staleScope = Inbound.Factory.Services.CreateScope();
        var staleLease = await staleScope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().ClaimAsync();
        Assert.Equal(BackgroundJobTypes.DiscoverInboundMessages, staleLease!.Type);

        Inbound.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await Inbound.RunJobsAsync() >= 1);
        var completed = (await Inbound.SyncStateAsync()).CoveredThrough;
        Assert.NotNull(completed);

        Inbound.Clock.Advance(TimeSpan.FromMinutes(1));
        var handler = staleScope.ServiceProvider.GetServices<IBackgroundJobHandler>().Single(h => h.Type == BackgroundJobTypes.DiscoverInboundMessages);
        await handler.HandleAsync(staleLease, CancellationToken.None);
        Assert.Equal(completed, (await Inbound.SyncStateAsync()).CoveredThrough);
    }

    [Fact] // AC 14
    public async Task Discovery_keeps_trust_checks_and_reports_errors_and_gaps()
    {
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));
        var box = await Inbound.CreateMailboxAsync();
        var other = await Inbound.CreateMailboxAsync();
        await Inbound.SetSyncAsync(Now.AddHours(-2), null);

        // Listed without recipients: quarantined, nothing fetched, until a verified webhook supplies the evidence.
        var silentId = InboundHarness.NewEmailId();
        Inbound.Api.List(silentId, Now, InboundHarness.Listed(silentId, Now));
        await Inbound.DiscoverAsync();
        await fx.WithDbAsync(async db =>
        {
            var message = await db.InboundMessages.SingleAsync(m => m.SourceMessageId == silentId);
            Assert.Equal((InboundRoutingState.MissingRecipientEvidence, (Guid?)null), (message.RoutingState, message.TenantId));
            Assert.False(await db.BackgroundJobs.AnyAsync(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == message.Id));
        });
        Assert.Empty((await Inbound.Client(box.Key).GetAsync("/v1/tenant/inbound-messages").ExpectAsync(HttpStatusCode.OK)).Items());

        Inbound.Api.Respond(silentId, FakeResendApi.Ok(InboundHarness.Content(silentId, [box.Address])));
        await Inbound.PostEventAsync(InboundHarness.ReceivedEvent(silentId, box.Address)).ExpectAsync(HttpStatusCode.OK);
        await Inbound.RunJobsAsync();
        var resolved = Assert.Single((await Inbound.Client(box.Key).GetAsync("/v1/tenant/inbound-messages").ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal(("routed", "available"), (resolved.Str("routing_state"), resolved.Str("content_state")));
        Assert.Empty((await Inbound.Client(other.Key).GetAsync("/v1/tenant/inbound-messages").ExpectAsync(HttpStatusCode.OK)).Items());

        // Rejected credentials are a visible failure and leave the boundary alone.
        var before = (await Inbound.SyncStateAsync()).CoveredThrough;
        Inbound.Api.ListOverride = (host, _) => host == "resend.fake" ? FakeResendApi.Status(HttpStatusCode.Unauthorized)() : null;
        try
        {
            await Inbound.DiscoverAsync();
        }
        finally
        {
            Inbound.Api.ListOverride = null;
        }

        var unauthorized = await Inbound.SyncStateAsync();
        Assert.Equal(("provider_unauthorized", before), (unauthorized.LastErrorCode, unauthorized.CoveredThrough));
        Assert.Equal(BackgroundJobState.Failed, (await LatestDiscoveryJobAsync()).State);

        // History older than the provider keeps is reported as a gap, never as covered.
        using (var scope = Inbound.Factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<InboundDiscoveryService>()
                .SetSyncFromAsync(InboundHarness.InstanceId, Now.AddDays(-40));
            Assert.Null(result.Error);
        }

        await Inbound.DiscoverAsync();
        var gap = await Inbound.SyncStateAsync();
        Assert.Null(gap.LastErrorCode);
        Assert.Contains("cannot be recovered", gap.CoverageGap);
    }

    /// <summary>PostgreSQL keeps microseconds.</summary>
    private static DateTimeOffset Microseconds(DateTimeOffset value) => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private async Task<BackgroundJob> LatestDiscoveryJobAsync()
    {
        var state = await Inbound.SyncStateAsync();
        BackgroundJob? job = null;
        await fx.WithDbAsync(async db => job = await db.BackgroundJobs
            .Where(j => j.Type == BackgroundJobTypes.DiscoverInboundMessages && j.TargetId == state.Id)
            .OrderByDescending(j => j.Generation).FirstAsync());
        return job!;
    }
}
