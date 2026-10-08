using System.Net;
using System.Text;
using Pairrot.Core.Webhooks;

namespace Pairrot.UnitTests;

/// <summary>Features 010 and 011 without a database: the signature scheme, destinations, and the retry schedule.</summary>
public class WebhookRulesTests
{
    /// <summary>The published test vector (docs/webhooks-receiver.md), computed independently with Python's hmac module.</summary>
    public const string VectorSecret = "pwhsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    public const string VectorDeliveryId = "01900000-0000-7000-8000-000000000001";
    public const long VectorTimestamp = 1791374400;
    public const string VectorBody = """{"id":"01900000-0000-7000-8000-0000000000aa","type":"payment_request.updated"}""";
    public const string VectorSignature = "IO/pJAG2O51hyi8WurN0lY2gbAnoj9z/aI2jU9YuTAQ=";

    private static readonly byte[] Secret = WebhookSignature.ParseSecret(VectorSecret)!;
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(VectorBody);
    private static readonly DateTimeOffset SignedAt = DateTimeOffset.FromUnixTimeSeconds(VectorTimestamp);

    [Fact]
    public void The_signature_matches_the_published_test_vector()
    {
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), Secret);
        Assert.Equal(VectorSecret, WebhookSignature.FormatSecret(Secret));
        Assert.Equal(VectorSignature, WebhookSignature.Sign(Secret, VectorDeliveryId, VectorTimestamp, Body));
        Assert.Equal($"v1,pwhk_a,{VectorSignature}", WebhookSignature.Header([("pwhk_a", Secret)], VectorDeliveryId, VectorTimestamp, Body));
    }

    [Fact]
    public void Verification_rejects_tampering_other_keys_and_stale_timestamps_and_accepts_any_current_key()
    {
        var keys = new Dictionary<string, byte[]> { ["pwhk_a"] = Secret };
        var header = $"v1,pwhk_old,{Convert.ToBase64String(new byte[32])} v1,pwhk_a,{VectorSignature}";
        var stamp = VectorTimestamp.ToString();

        Assert.True(WebhookSignature.Verify(keys, VectorDeliveryId, stamp, header, Body, SignedAt.AddMinutes(4)));
        Assert.False(WebhookSignature.Verify(keys, VectorDeliveryId, stamp, header, Body, SignedAt.AddMinutes(6)));
        Assert.False(WebhookSignature.Verify(keys, VectorDeliveryId, stamp, header, Body, SignedAt.AddMinutes(-6)));
        Assert.False(WebhookSignature.Verify(keys, VectorDeliveryId, stamp, header, Encoding.UTF8.GetBytes(VectorBody + " "), SignedAt));
        Assert.False(WebhookSignature.Verify(keys, "other-delivery", stamp, header, Body, SignedAt));
        Assert.False(WebhookSignature.Verify(keys, VectorDeliveryId, (VectorTimestamp + 1).ToString(), header, Body, SignedAt));
        Assert.False(WebhookSignature.Verify(new Dictionary<string, byte[]> { ["pwhk_b"] = Secret }, VectorDeliveryId, stamp, header, Body, SignedAt));
        Assert.False(WebhookSignature.Verify(keys, VectorDeliveryId, "abc", header, Body, SignedAt));
        Assert.False(WebhookSignature.Verify(keys, VectorDeliveryId, stamp, null, Body, SignedAt));
    }

    [Theory]
    [InlineData("https://hooks.nalekci.cz/pairrot", null)]
    [InlineData("http://hooks.nalekci.cz/pairrot", "url must use HTTPS.")]
    [InlineData("https://user:pw@hooks.nalekci.cz/", "url must not contain credentials or a fragment.")]
    [InlineData("https://hooks.nalekci.cz/#x", "url must not contain credentials or a fragment.")]
    [InlineData("https://127.0.0.1/", "url must point to a public address.")]
    [InlineData("https://10.1.2.3/", "url must point to a public address.")]
    [InlineData("https://169.254.169.254/latest/meta-data", "url must point to a public address.")]
    [InlineData("https://[::1]/", "url must point to a public address.")]
    [InlineData("https://[::ffff:192.168.1.1]/", "url must point to a public address.")]
    [InlineData("https://[fd00::1]/", "url must point to a public address.")]
    [InlineData("https://localhost/", "url must point to a public host name.")]
    [InlineData("https://printer.local/", "url must point to a public host name.")]
    [InlineData("https://intranet/", "url must point to a public host name.")]
    [InlineData("hooks.nalekci.cz", "url must be an absolute HTTPS URL.")]
    public void Destinations_must_be_public_https(string url, string? error)
    {
        Assert.Equal(error, WebhookDestinations.Validate(url, new HashSet<string>()));
    }

    [Fact]
    public void A_development_allowlist_admits_only_its_exact_host_and_port()
    {
        var allowed = new HashSet<string> { "localhost:5099" };
        Assert.Null(WebhookDestinations.Validate("http://localhost:5099/hook", allowed));
        Assert.NotNull(WebhookDestinations.Validate("http://localhost:5098/hook", allowed));
        Assert.NotNull(WebhookDestinations.Validate("http://127.0.0.1:5099/hook", allowed));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2a00:1450:4014:80c::200e", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    public void Resolved_addresses_are_checked(string address, bool isPublic)
    {
        Assert.Equal(isPublic, WebhookDestinations.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public void Four_attempts_with_retries_after_5_seconds_60_seconds_and_1_hour()
    {
        var failed = DateTimeOffset.Parse("2026-10-07T12:00:00Z");
        Assert.Equal(failed.AddSeconds(5), WebhookRetryPolicy.NextAttempt(1, failed));
        Assert.Equal(failed.AddSeconds(60), WebhookRetryPolicy.NextAttempt(2, failed));
        Assert.Equal(failed.AddHours(1), WebhookRetryPolicy.NextAttempt(3, failed));
        Assert.Null(WebhookRetryPolicy.NextAttempt(4, failed));

        // Retry-After may delay but never accelerate an attempt, and malformed values are ignored.
        Assert.Equal(failed.AddSeconds(120), WebhookRetryPolicy.NextAttempt(1, failed, WebhookRetryPolicy.RetryAfter("120", failed)));
        Assert.Equal(failed.AddSeconds(60), WebhookRetryPolicy.NextAttempt(2, failed, WebhookRetryPolicy.RetryAfter("1", failed)));
        Assert.Null(WebhookRetryPolicy.RetryAfter("soon", failed));
        Assert.Null(WebhookRetryPolicy.RetryAfter("99999999999999999999", failed));
        Assert.Equal(failed.AddMinutes(10), WebhookRetryPolicy.RetryAfter(failed.AddMinutes(10).ToString("r"), failed));
    }

    [Theory]
    [InlineData(200, WebhookAttemptOutcome.Delivered)]
    [InlineData(204, WebhookAttemptOutcome.Delivered)]
    [InlineData(408, WebhookAttemptOutcome.RetryableFailure)]
    [InlineData(425, WebhookAttemptOutcome.RetryableFailure)]
    [InlineData(429, WebhookAttemptOutcome.RetryableFailure)]
    [InlineData(503, WebhookAttemptOutcome.RetryableFailure)]
    [InlineData(400, WebhookAttemptOutcome.PermanentFailure)]
    [InlineData(410, WebhookAttemptOutcome.PermanentFailure)]
    public void Responses_are_classified(int status, WebhookAttemptOutcome outcome)
    {
        Assert.Equal(outcome, WebhookRetryPolicy.Classify(status));
    }

    [Fact] // Feature 016
    public void Event_types_are_validated_deduplicated_and_sorted()
    {
        Assert.Equal(["payment_request.updated", "transaction.recorded"],
            WebhookEvents.ParseTypes(["transaction.recorded", "payment_request.updated", "transaction.recorded"]).Types!);
        Assert.NotNull(WebhookEvents.ParseTypes([]).Error);
        Assert.NotNull(WebhookEvents.ParseTypes(["webhook.test"]).Error);
        Assert.NotNull(WebhookEvents.ParseTypes(["bank_connection.reception_changed"]).Error);
        Assert.NotNull(WebhookEvents.ParseTypes([null]).Error);
    }

    [Fact] // Feature 016
    public void A_subject_event_has_the_feature_010_envelope_and_its_digest()
    {
        var id = Guid.Parse("01900000-0000-7000-8000-0000000000bb");
        var subject = Guid.Parse("01900000-0000-7000-8000-0000000000cc");
        var tenant = Guid.Parse("01900000-0000-7000-8000-0000000000dd");
        var ev = WebhookEvents.ForSubject(id, WebhookEvents.InboundAddressPaused, tenant, subject, 3, Core.Inbound.InboundEnvironment.Live,
            DateTimeOffset.Parse("2026-10-08T09:12:00.5Z"), new { subject_id = subject, sequence = 3, cause = WebhookEvents.Causes.ForeignMailLimit });

        Assert.Equal(
            "{\"id\":\"01900000-0000-7000-8000-0000000000bb\",\"type\":\"inbound_address.paused\",\"schema_version\":1,"
            + "\"created_at\":\"2026-10-08T09:12:00.5Z\",\"environment\":\"live\",\"tenant_id\":\"01900000-0000-7000-8000-0000000000dd\","
            + "\"data\":{\"subject_id\":\"01900000-0000-7000-8000-0000000000cc\",\"sequence\":3,\"cause\":\"foreign_mail_limit\"}}",
            ev.Body);
        Assert.Equal((subject, 3, WebhookEvents.Sha256(ev.Body!)), (ev.SubjectId!.Value, ev.Sequence!.Value, ev.BodySha256));
        Assert.Null(ev.PaymentChangeId);
    }
}
