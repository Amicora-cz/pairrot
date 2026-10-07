namespace Pairrot.Core.Banking;

/// <summary>
/// A tenant's bank account. Its notification addresses live in <c>inbound_addresses</c>.
/// The owner, account, and currency never change; to use another account, create a new
/// connection and archive this one.
/// </summary>
public class BankConnection
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string? Label { get; set; }

    /// <summary>Normalized domestic form, e.g. <c>19-2000145399/0800</c>.</summary>
    public required string AccountNumber { get; set; }

    /// <summary>Normalized electronic IBAN; the account identity, unique across all tenants.</summary>
    public required string Iban { get; set; }

    public string Currency { get; set; } = CzechBankAccount.Currency;
    public BankConnectionStatus Status { get; set; } = BankConnectionStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public enum BankConnectionStatus
{
    Active,
    Archived,
}
