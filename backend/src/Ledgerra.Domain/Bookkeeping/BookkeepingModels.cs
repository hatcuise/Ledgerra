namespace Ledgerra.Domain.Bookkeeping;

public sealed class ImportProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string SettingsJson { get; set; } = "{}";
}

public sealed class Payee
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public Guid? DefaultCategoryId { get; set; }
}

public sealed class EntryMetadata
{
    public Guid Id { get; set; } // transaction ID
    public Guid UserId { get; set; }
    public Guid? PayeeId { get; set; }
    public string? ExternalKey { get; set; }
    public string Reference { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Status { get; set; } = "Uncleared";
    public Guid? ReconciliationId { get; set; }
}

public sealed class Reconciliation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public DateOnly StatementDate { get; set; }
    public decimal StatementBalance { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

public sealed class TransferMatch
{
    public Guid Id { get; set; } // transfer group
    public Guid UserId { get; set; }
    public Guid OutgoingId { get; set; }
    public Guid IncomingId { get; set; }
    public Guid? OutgoingCategoryId { get; set; }
    public Guid? IncomingCategoryId { get; set; }
    public Transactions.TransactionType OutgoingType { get; set; }
    public Transactions.TransactionType IncomingType { get; set; }
    public bool Automatic { get; set; }
    public DateTime MatchedAtUtc { get; set; }
}
