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
    /// The next candidate in the current YYYYNNNNNN series, or that year's maximum plus one
    /// when exhausted. A value outside the current year's series starts at its 000001;
    /// 1 is the initial/legacy cursor. Every saved symbol remains reserved across year changes.
    /// </summary>
    public long NextCandidate { get; set; } = 1;
}
