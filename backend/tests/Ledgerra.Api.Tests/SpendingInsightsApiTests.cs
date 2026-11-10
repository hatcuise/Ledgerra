using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ledgerra.Domain.Budgets;
using Ledgerra.Domain.ExchangeRates;
using Ledgerra.Domain.Insights;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Ledgerra.Api.Tests;

public sealed class SpendingInsightsApiTests(LedgerraApiFactory factory) : IClassFixture<LedgerraApiFactory>
{
    private async Task<HttpClient> Client()
    {
        var c = factory.CreateClient(); var r = await c.PostAsJsonAsync("/api/auth/register", new { login = "insight-" + Guid.NewGuid().ToString("N")[..12], password = "P@ssw0rd123!" }); r.EnsureSuccessStatusCode();
        var auth = await r.Content.ReadFromJsonAsync<JsonElement>(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString()); return c;
    }
    private static async Task<JsonElement> Post(HttpClient c, string path, object value) { var r = await c.PostAsJsonAsync(path, value); r.EnsureSuccessStatusCode(); return await r.Content.ReadFromJsonAsync<JsonElement>(); }
    private static Guid Id(JsonElement row) => row.GetProperty("id").GetGuid();
    private static Task<JsonElement> Account(HttpClient c, string currency = "USD", bool excluded = false) => Post(c, "/api/accounts", new { name = "Insights bank", type = "Checking", currencyCode = currency, openingBalance = 10000, excludeFromBudget = excluded });
    private static Task<JsonElement> Category(HttpClient c) => Post(c, "/api/categories", new { name = "Insight food", kind = "Expense" });
    private static Task<JsonElement> Entry(HttpClient c, Guid account, Guid category, decimal amount, string date = "2026-04-10T12:00:00Z", string type = "Expense") => Post(c, "/api/transactions", new { accountId = account, categoryId = category, amount, type, occurredOnUtc = date, note = "Insight market" });
    private static Task<SpendingInsights?> Insights(HttpClient c, string month = "2026-04", string zone = "UTC") => c.GetFromJsonAsync<SpendingInsights>($"/api/insights?month={month}&timeZone={zone}");

    [Fact]
    public async Task Insights_AreUserScopedExcludeOptedOutAccountsAndDoNotWriteFinancialData()
    {
        using var c = await Client(); using var other = await Client(); var category = Id(await Category(c)); var account = Id(await Account(c)); var excluded = Id(await Account(c, excluded: true));
        await Entry(c, account, category, 100); await Entry(c, account, category, 20, type: "Refund"); await Entry(c, excluded, category, 9000);
        var result = (await Insights(c))!; Assert.Equal(80, result.NetSpending); Assert.Equal(80, result.Categories.Single().Spent); Assert.True(result.DataComplete); Assert.Empty(result.Alerts);
        Assert.Equal(0, (await Insights(other))!.NetSpending);
        Assert.Equal(9920, (await c.GetFromJsonAsync<JsonElement>($"/api/accounts/{account}")).GetProperty("currentBalance").GetDecimal());
        Assert.Equal(3, (await c.GetFromJsonAsync<JsonElement>("/api/transactions")).GetArrayLength());
    }

    [Fact]
    public async Task BudgetCarryover_DismissalEscalationAndBackupRoundTripWorkTogether()
    {
        using var c = await Client(); using var other = await Client(); var category = Id(await Category(c)); var account = Id(await Account(c));
        await Entry(c, account, category, 20, "2026-02-10T12:00:00Z"); await Entry(c, account, category, 20, "2026-03-10T12:00:00Z"); await Entry(c, account, category, 240);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LedgerraDbContext>(); var owner = (await db.Categories.SingleAsync(x => x.Id == category)).UserId;
            foreach (var month in new[] { 2, 3, 4 }) { var period = new BudgetPeriod { Id = Guid.NewGuid(), UserId = owner, Year = 2026, Month = month }; db.BudgetPeriods.Add(period); db.BudgetCategoryLimits.Add(new BudgetCategoryLimit { Id = Guid.NewGuid(), BudgetPeriodId = period.Id, CategoryId = category, PlannedAmount = 100, CarryOverUnspent = true }); } await db.SaveChangesAsync();
        }
        var result = (await Insights(c))!; Assert.Equal(260, result.Categories.Single().BudgetAvailable);
        var near = result.Alerts.Single(x => x.Kind == "BudgetWarning"); Assert.False(near.Dismissed);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/insights/dismiss", new { key = near.Key, month = "2026-04" })).StatusCode);
        (await c.PostAsJsonAsync("/api/insights/dismiss", new { key = near.Key, month = "2026-04" })).EnsureSuccessStatusCode();
        Assert.True((await Insights(c))!.Alerts.Single(x => x.Key == near.Key).Dismissed);
        var prefs = (await c.GetFromJsonAsync<InsightPreferences>("/api/insights/preferences"))!; prefs.LargePurchaseThreshold = 700; (await c.PutAsJsonAsync("/api/insights/preferences", prefs)).EnsureSuccessStatusCode();
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export"); Assert.Equal(7, backup.GetProperty("version").GetInt32()); (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        Assert.True((await Insights(c))!.Alerts.Single(x => x.Key == near.Key).Dismissed); Assert.Equal(700, (await c.GetFromJsonAsync<InsightPreferences>("/api/insights/preferences"))!.LargePurchaseThreshold);
        await Entry(c, account, category, 60); result = (await Insights(c))!;
        var over = result.Alerts.Single(x => x.Kind == "BudgetExceeded"); Assert.False(over.Dismissed); Assert.NotEqual(near.Key, over.Key);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/insights/dismiss/{near.Key}")).StatusCode);
        (await c.DeleteAsync($"/api/insights/dismiss/{near.Key}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MissingFx_PausesAlertsAndSupplyingRateRestoresCompleteTotals()
    {
        using var c = await Client(); var category = Id(await Category(c)); var usd = Id(await Account(c)); var eur = Id(await Account(c, "EUR"));
        await Entry(c, usd, category, 1000); await Entry(c, eur, category, 50);
        var result = (await Insights(c))!; Assert.False(result.DataComplete); Assert.Equal(1, result.ExcludedEntries); Assert.Equal(1000, result.NetSpending); Assert.Empty(result.Alerts); Assert.Contains(result.Warnings, x => x.Contains("Missing FX"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LedgerraDbContext>(); var owner = (await db.Accounts.SingleAsync(x => x.Id == usd)).UserId;
            db.ExchangeRates.Add(new UserExchangeRate { Id = Guid.NewGuid(), UserId = owner, FromCurrencyCode = "EUR", ToCurrencyCode = "USD", Month = new DateOnly(2026, 4, 1), Rate = 1.1m, UpdatedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync();
        }
        result = (await Insights(c))!; Assert.True(result.DataComplete); Assert.Equal(1055, result.NetSpending); Assert.Contains(result.Alerts, x => x.Kind == "LargePurchase");
    }

    [Fact]
    public async Task BrowserTimeZone_UsesLocalMonthBoundariesAndDoesNotChangeStoredTimestamps()
    {
        using var c = await Client(); var category = Id(await Category(c)); var account = Id(await Account(c));
        var entry = await Entry(c, account, category, 40, "2026-05-01T02:00:00Z");
        Assert.Equal(0, (await Insights(c))!.NetSpending); var local = (await Insights(c, zone: "America/New_York"))!; Assert.Equal(40, local.NetSpending); Assert.Equal("America/New_York", local.TimeZone);
        Assert.Equal(0, (await Insights(c, "2026-05", "America/New_York"))!.NetSpending);
        var evidence = await c.GetFromJsonAsync<JsonElement>("/api/transactions?from=2026-04-01&to=2026-04-30&timeZone=America%2FNew_York");
        Assert.Contains(evidence.EnumerateArray(), x => Id(x) == Id(entry));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/transactions?timeZone=Not/AZone")).StatusCode);
        var stored = await c.GetFromJsonAsync<JsonElement>($"/api/transactions/{Id(entry)}"); Assert.Equal(DateTime.Parse("2026-05-01T02:00:00Z").ToUniversalTime(), stored.GetProperty("occurredOnUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/insights?month=2026-04&timeZone=Not/AZone")).StatusCode);
    }

    [Fact]
    public async Task PreferencesAndDismissals_ValidateAndResetWithoutAffectingOtherUsers()
    {
        using var c = await Client(); using var other = await Client(); var category = Id(await Category(c)); var account = Id(await Account(c)); await Entry(c, account, category, 700);
        var prefs = new InsightPreferences { UserId = Guid.NewGuid(), LargePurchaseThreshold = 600 }; (await c.PutAsJsonAsync("/api/insights/preferences", prefs)).EnsureSuccessStatusCode();
        Assert.Equal(500, (await other.GetFromJsonAsync<InsightPreferences>("/api/insights/preferences"))!.LargePurchaseThreshold);
        prefs.BudgetWarningPercent = 101; Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/insights/preferences", prefs)).StatusCode);
        var alert = (await Insights(c))!.Alerts.Single(x => x.Kind == "LargePurchase"); (await c.PostAsJsonAsync("/api/insights/dismiss", new { key = alert.Key, month = "2026-04" })).EnsureSuccessStatusCode();
        var backup = JsonNode.Parse(await (await c.GetAsync("/api/backup/export")).Content.ReadAsStringAsync())!;
        backup["insightPreferences"]!["minimumSpendingIncrease"] = 0;
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/backup/restore", backup)).StatusCode); Assert.Equal(700, (await Insights(c))!.NetSpending);
        (await c.DeleteAsync("/api/settings/account-data")).EnsureSuccessStatusCode(); Assert.Equal(500, (await c.GetFromJsonAsync<InsightPreferences>("/api/insights/preferences"))!.LargePurchaseThreshold);
        Assert.Empty((await Insights(c))!.Alerts); Assert.Equal(0, (await Insights(c))!.NetSpending);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/insights?month=not-a-month")).StatusCode);
    }
}
