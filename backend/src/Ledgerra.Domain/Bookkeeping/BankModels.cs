namespace Ledgerra.Domain.Bookkeeping;

public sealed class BankProviderSettings
{
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = "";
    public string EncryptedSecret { get; set; } = "";
    public string Environment { get; set; } = "Sandbox";
    public string Country { get; set; } = "US";
    public string? RedirectUri { get; set; }
}
public sealed class BankConnection
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ItemId { get; set; } = "";
    public string EncryptedAccessToken { get; set; } = "";
    public string Environment { get; set; } = "Sandbox";
    public string Name { get; set; } = "Bank connection";
    public string Cursor { get; set; } = "";
    public DateTime? LastSyncedAtUtc { get; set; }
}
public sealed class ConnectedBankAccount
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ProviderAccountId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mask { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal? ReportedBalance { get; set; }
    public Guid? LedgerAccountId { get; set; }
}
public sealed class BankFeedEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ProviderTransactionId { get; set; } = "";
    public string ProviderAccountId { get; set; } = "";
    public string ExternalKey { get; set; } = "";
    public DateTime DateUtc { get; set; }
    public decimal Amount { get; set; } // Plaid: positive is money leaving the account.
    public string Currency { get; set; } = "";
    public string Name { get; set; } = "";
    public string Payee { get; set; } = "";
    public bool Pending { get; set; }
    public bool Removed { get; set; }
    public bool Modified { get; set; }
}
