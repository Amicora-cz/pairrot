namespace Pairrot.Core.Tenants;

public class TenantAuditEvent
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? ActorApiKeyId { get; set; }
    public string? ActorDescription { get; set; }
    public Guid TargetTenantId { get; set; }
    public required string Action { get; set; }

    /// <summary>JSON object with change details. Never contains secrets or secret hashes.</summary>
    public string Details { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

public static class AuditActions
{
    public const string TenantCreated = "tenant.created";
    public const string TenantConfigurationUpdated = "tenant.configuration_updated";
    public const string TenantStatusChanged = "tenant.status_changed";
    public const string TenantDeleted = "tenant.deleted";
    public const string ApiKeyIssued = "api_key.issued";
    public const string ApiKeyRevoked = "api_key.revoked";
    public const string MembershipAdded = "membership.added";
    public const string MembershipRemoved = "membership.removed";
    public const string BankConnectionCreated = "bank_connection.created";
    public const string BankConnectionLabelChanged = "bank_connection.label_changed";
    public const string BankConnectionAddressAssigned = "bank_connection.address_assigned";
    public const string BankConnectionAddressReplaced = "bank_connection.address_replaced";
    public const string BankConnectionArchived = "bank_connection.archived";
    public const string InboundMessageFetchRetried = "inbound_message.fetch_retried";
    public const string InboundMessageRouted = "inbound_message.routed";
    public const string InboundMessageReprocessRequested = "inbound_message.reprocess_requested";
    public const string InboundAddressPaused = "inbound_address.paused";
    public const string InboundAddressResumed = "inbound_address.resumed";
    public const string ForwardingSourceRegistered = "forwarding_source.registered";
    public const string ForwardingSourceRemoved = "forwarding_source.removed";
    public const string PaymentRequestCreated = "payment_request.created";
    public const string PaymentRequestUpdated = "payment_request.updated";
    public const string PaymentRequestCancelled = "payment_request.cancelled";
    public const string PaymentAllocated = "payment_allocation.created";
    public const string PaymentAllocationRevoked = "payment_allocation.revoked";
    public const string TransactionIgnored = "transaction.ignored";
    public const string TransactionRestored = "transaction.restored";
    public const string TransactionAutoMatchingEnabled = "transaction.auto_matching_enabled";
    public const string TransactionMatchRequested = "transaction.match_requested";
    public const string ReviewCaseResolved = "review_case.resolved";
    public const string ReviewCaseReopened = "review_case.reopened";
}
