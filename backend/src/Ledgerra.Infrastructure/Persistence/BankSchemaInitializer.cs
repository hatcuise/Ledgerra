using Microsoft.EntityFrameworkCore;
namespace Ledgerra.Infrastructure.Persistence;
public static class BankSchemaInitializer
{
    public static async Task InitializeAsync(LedgerraDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return;
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "BankProviderSettings" (
                "UserId" uuid PRIMARY KEY, "ClientId" varchar(200) NOT NULL, "EncryptedSecret" varchar(4096) NOT NULL,
                "Environment" varchar(20) NOT NULL, "Country" varchar(2) NOT NULL, "RedirectUri" varchar(2048) NULL);
            CREATE TABLE IF NOT EXISTS "BankConnections" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL REFERENCES "BankProviderSettings" ("UserId") ON DELETE RESTRICT, "ItemId" varchar(200) NOT NULL,
                "EncryptedAccessToken" varchar(4096) NOT NULL, "Environment" varchar(20) NOT NULL, "Name" varchar(120) NOT NULL,
                "Cursor" varchar(4096) NOT NULL, "LastSyncedAtUtc" timestamp with time zone NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankConnections_UserId_ItemId" ON "BankConnections" ("UserId", "ItemId");
            CREATE TABLE IF NOT EXISTS "ConnectedBankAccounts" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "ConnectionId" uuid NOT NULL REFERENCES "BankConnections" ("Id") ON DELETE CASCADE,
                "ProviderAccountId" varchar(200) NOT NULL, "Name" varchar(120) NOT NULL, "Mask" varchar(8) NOT NULL,
                "Currency" varchar(3) NOT NULL, "Type" varchar(40) NOT NULL, "ReportedBalance" numeric(18,2) NULL, "LedgerAccountId" uuid NULL REFERENCES "Accounts" ("Id") ON DELETE SET NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ConnectedBankAccounts_ConnectionId_ProviderAccountId" ON "ConnectedBankAccounts" ("ConnectionId", "ProviderAccountId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ConnectedBankAccounts_UserId_LedgerAccountId" ON "ConnectedBankAccounts" ("UserId", "LedgerAccountId") WHERE "LedgerAccountId" IS NOT NULL;
            CREATE TABLE IF NOT EXISTS "BankFeedEntries" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "ConnectionId" uuid NOT NULL REFERENCES "BankConnections" ("Id") ON DELETE CASCADE,
                "ProviderTransactionId" varchar(200) NOT NULL, "ProviderAccountId" varchar(200) NOT NULL, "ExternalKey" varchar(64) NOT NULL,
                "DateUtc" timestamp with time zone NOT NULL, "Amount" numeric(18,2) NOT NULL, "Currency" varchar(3) NOT NULL,
                "Name" varchar(400) NOT NULL, "Payee" varchar(120) NOT NULL, "Pending" boolean NOT NULL, "Removed" boolean NOT NULL, "Modified" boolean NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankFeedEntries_ConnectionId_ProviderTransactionId" ON "BankFeedEntries" ("ConnectionId", "ProviderTransactionId");
            """, ct);
    }
}
