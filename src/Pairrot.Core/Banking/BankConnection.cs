namespace Pairrot.Core.Banking;

/// <summary>
/// One physical bank account (Feature 013), shared by every registration of it in any tenant.
/// Created on the first registration and never deleted: variable symbols, bank references, and
/// lookalike comparison are keyed by the account, so they stay unique per real account however
/// often it is archived and registered again. The retained identity is not anonymous; Feature 014
/// documents the erasure exception.
/// </summary>
public class BankAccount
{
    public Guid Id { get; set; }

    /// <summary>Normalized electronic IBAN; unique.</summary>
    public required string Iban { get; set; }

    /// <summary>Normalized domestic form, e.g. <c>19-2000145399/0800</c>.</summary>
    public required string AccountNumber { get; set; }

    public required string BankCode { get; set; }
    public string Currency { get; set; } = CzechBankAccount.Currency;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A tenant's registration of a bank account. Its notification addresses live in
/// <c>inbound_addresses</c>. The owner, account, and currency never change; to use another
/// account, create a new connection and archive this one. At most one registration per account is
/// active in the whole system; archiving or deleting the tenant releases the account.
/// </summary>
public class BankConnection
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The physical account (Feature 013).</summary>
    public Guid BankAccountId { get; set; }

    public string? Label { get; set; }

    /// <summary>Snapshot of the account's domestic form, e.g. <c>19-2000145399/0800</c>.</summary>
    public required string AccountNumber { get; set; }

    /// <summary>Snapshot of the account's normalized electronic IBAN.</summary>
    public required string Iban { get; set; }

    public string Currency { get; set; } = CzechBankAccount.Currency;
    public BankConnectionStatus Status { get; set; } = BankConnectionStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>Why the connection is archived; <c>null</c> while it is active.</summary>
    public BankConnectionArchivedReason? ArchivedReason { get; set; }

    /// <summary>Feature 014: the label is gone and the account snapshots are masked; it can never be restored.</summary>
    public DateTimeOffset? ErasedAt { get; set; }
}

public enum BankConnectionStatus
{
    Active,
    Archived,
}

public enum BankConnectionArchivedReason
{
    /// <summary>The tenant or its parent archived it.</summary>
    User,

    /// <summary>The tenant was deleted, which releases its accounts.</summary>
    TenantDeleted,

    /// <summary>An operator released it (<c>accounts release</c>).</summary>
    Operator,
}
