namespace Pairrot.Core.Payments;

/// <summary>
/// Automatic variable symbols of one physical bank account (Features 012 and 013). Creation of
/// every payment request on the account, automatic or supplied, through any of its registrations,
/// locks this row until it commits, so symbol choice is serialized per account across processes.
/// Created on first use.
/// </summary>
public class VariableSymbolAllocator
{
    public Guid BankAccountId { get; set; }

    /// <summary>
    /// The lowest symbol automatic assignment considers next; lower symbols are taken or were
    /// skipped. Starts at 1, only moves up, and equals
    /// <see cref="PaymentRequestRules.ExhaustedVariableSymbolCursor"/> once nothing is left.
    /// </summary>
    public long NextCandidate { get; set; } = 1;
}
