namespace Pairrot.Core.Payments;

/// <summary>
/// Automatic variable symbols of one bank connection (Feature 012). Creation of every payment
/// request on the connection, automatic or supplied, locks this row until it commits, so symbol
/// choice is serialized per connection across processes. Created on first use.
/// </summary>
public class VariableSymbolAllocator
{
    public Guid BankConnectionId { get; set; }

    /// <summary>
    /// The lowest symbol automatic assignment considers next; lower symbols are taken or were
    /// skipped. Starts at 1, only moves up, and equals
    /// <see cref="PaymentRequestRules.ExhaustedVariableSymbolCursor"/> once nothing is left.
    /// </summary>
    public long NextCandidate { get; set; } = 1;
}
