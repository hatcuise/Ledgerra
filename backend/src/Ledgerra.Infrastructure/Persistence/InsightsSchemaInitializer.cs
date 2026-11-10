using Microsoft.EntityFrameworkCore;
namespace Ledgerra.Infrastructure.Persistence;
public static class InsightsSchemaInitializer
{
    public static async Task InitializeAsync(LedgerraDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return;
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "InsightPreferences" (
                "UserId" uuid PRIMARY KEY, "BudgetAlerts" boolean NOT NULL, "SpendingChangeAlerts" boolean NOT NULL,
                "LargePurchaseAlerts" boolean NOT NULL, "UnusualPurchaseAlerts" boolean NOT NULL,
                "BudgetWarningPercent" numeric(18,2) NOT NULL, "SpendingIncreasePercent" numeric(18,2) NOT NULL,
                "MinimumSpendingIncrease" numeric(18,2) NOT NULL, "LargePurchaseThreshold" numeric(18,2) NOT NULL,
                "UnusualPurchaseMinimum" numeric(18,2) NOT NULL, "UnusualPurchaseMultiplier" numeric(18,2) NOT NULL);
            CREATE TABLE IF NOT EXISTS "InsightDismissals" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "AlertKey" varchar(64) NOT NULL,
                "Month" varchar(7) NOT NULL, "DismissedAtUtc" timestamp with time zone NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_InsightDismissals_UserId_AlertKey" ON "InsightDismissals" ("UserId", "AlertKey");
            """, ct);
    }
}
