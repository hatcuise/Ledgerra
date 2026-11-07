using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public static class BookkeepingSchemaInitializer
{
    public static async Task InitializeAsync(LedgerraDbContext db, CancellationToken token = default)
    {
        if (!db.Database.IsRelational()) return;
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "RecurringTransactionTemplates" ADD COLUMN IF NOT EXISTS "DestinationAccountId" uuid NULL REFERENCES "Accounts" ("Id") ON DELETE RESTRICT;
            CREATE TABLE IF NOT EXISTS "ImportProfiles" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "Name" varchar(120) NOT NULL, "SettingsJson" varchar(8000) NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImportProfiles_UserId_Name" ON "ImportProfiles" ("UserId", "Name");
            CREATE TABLE IF NOT EXISTS "Payees" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "Name" varchar(120) NOT NULL, "DefaultCategoryId" uuid NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Payees_UserId_Name" ON "Payees" ("UserId", "Name");
            CREATE TABLE IF NOT EXISTS "EntryMetadata" (
                "Id" uuid PRIMARY KEY REFERENCES "Transactions" ("Id") ON DELETE CASCADE, "UserId" uuid NOT NULL,
                "PayeeId" uuid NULL, "Reference" varchar(120) NOT NULL, "Tags" varchar(400) NOT NULL,
                "Status" varchar(20) NOT NULL, "ReconciliationId" uuid NULL);
            CREATE TABLE IF NOT EXISTS "Reconciliations" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "AccountId" uuid NOT NULL, "StatementDate" date NOT NULL,
                "StatementBalance" numeric(18,2) NOT NULL, "CompletedAtUtc" timestamp with time zone NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Reconciliations_UserId_AccountId_StatementDate" ON "Reconciliations" ("UserId", "AccountId", "StatementDate");
            """, token);
    }
}
