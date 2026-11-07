using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Tests;

public sealed class LedgerToolsTests(LedgerraApiFactory factory) : IClassFixture<LedgerraApiFactory>
{
    private async Task<HttpClient> Client()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new { login = "tools-" + Guid.NewGuid().ToString("N")[..12], password = "P@ssw0rd123!" });
        response.EnsureSuccessStatusCode(); var auth = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString()); return client;
    }
    private static async Task<JsonElement> Post(HttpClient c, string path, object value)
    { var r = await c.PostAsJsonAsync(path, value); r.EnsureSuccessStatusCode(); return await r.Content.ReadFromJsonAsync<JsonElement>(); }
    private static Guid Id(JsonElement row) => row.GetProperty("id").GetGuid();
    private static Task<JsonElement> Account(HttpClient c) => Post(c, "/api/accounts", new { name = "Tools bank", type = "Checking", currencyCode = "USD", openingBalance = 100 });
    private static Task<JsonElement> Category(HttpClient c, string name, string kind = "Expense") => Post(c, "/api/categories", new { name, kind });
    private static Task<JsonElement> Entry(HttpClient c, Guid account, Guid? category = null, string note = "Market", string type = "Expense") => Post(c, "/api/transactions", new { accountId = account, categoryId = category, amount = 30, type, occurredOnUtc = "2026-04-10T12:00:00Z", note });

    [Fact]
    public async Task SplitEditor_RoundTripsAllocationsAndMetadataWithoutDoubleCounting()
    {
        using var c = await Client(); var account = Id(await Account(c)); var a = Id(await Category(c, "Food")); var b = Id(await Category(c, "Household"));
        var payee = Id(await Post(c, "/api/bookkeeping/payees", new { name = "Market", defaultCategoryId = a }));
        var entry = await Post(c, "/api/transactions", new { accountId = account, amount = 30, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z", note = "Split shop", updateMetadata = true, payeeId = payee, reference = "R123", tags = "home, food", paymentMethod = "Card", status = "Cleared", splitLines = new[] { new { categoryId = a, amount = 10 }, new { categoryId = b, amount = 20 } } });
        var details = await c.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/entries/{Id(entry)}/details");
        Assert.Equal(2, details.GetProperty("splitLines").GetArrayLength()); Assert.Equal("Card", details.GetProperty("metadata").GetProperty("paymentMethod").GetString());
        var register = await c.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{account}/register"); Assert.Equal(70, register.GetProperty("balance").GetDecimal()); Assert.Single(register.GetProperty("entries").EnumerateArray());
        var filtered = await c.GetFromJsonAsync<JsonElement>($"/api/transactions?categoryId={b}"); Assert.Contains(filtered.EnumerateArray(), x => Id(x) == Id(entry));
        var updated = await c.PutAsJsonAsync($"/api/transactions/{Id(entry)}", new { amount = 30, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z", splitLines = new[] { new { categoryId = a, amount = 12 }, new { categoryId = b, amount = 18 } } });
        updated.EnsureSuccessStatusCode(); var replacement = await updated.Content.ReadFromJsonAsync<JsonElement>();
        details = await c.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/entries/{Id(replacement)}/details"); Assert.Equal("R123", details.GetProperty("metadata").GetProperty("reference").GetString()); Assert.Equal("Card", details.GetProperty("metadata").GetProperty("paymentMethod").GetString());
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export"); (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        Assert.Equal(70, (await c.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{account}/register")).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task MetadataAndSplits_RejectForeignPayeesAndInvalidAmountsBeforePosting()
    {
        using var c = await Client(); using var other = await Client(); var account = Id(await Account(c));
        var payee = Id(await Post(other, "/api/bookkeeping/payees", new { name = "Foreign" }));
        var r = await c.PostAsJsonAsync("/api/transactions", new { accountId = account, amount = 30, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z", updateMetadata = true, payeeId = payee }); Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var income = Id(await Category(c, "Salary", "Income")); var expense = Id(await Category(c, "Food"));
        r = await c.PostAsJsonAsync("/api/transactions", new { accountId = account, amount = 30, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z", splitLines = new[] { new { categoryId = income, amount = 10 }, new { categoryId = expense, amount = 20 } } }); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await c.PostAsJsonAsync("/api/transactions", new { accountId = account, amount = 30.001, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z" }); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/transactions")).EnumerateArray());
    }

    [Fact]
    public async Task ReconciledMetadataAndSplitChildren_CannotBeEditedDirectly()
    {
        using var c = await Client(); var account = Id(await Account(c)); var a = Id(await Category(c, "Food")); var b = Id(await Category(c, "Other"));
        var entry = await Post(c, "/api/transactions", new { accountId = account, amount = 30, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z", splitLines = new[] { new { categoryId = a, amount = 10 }, new { categoryId = b, amount = 20 } } });
        var entries = await c.GetFromJsonAsync<JsonElement>("/api/transactions"); var child = entries.EnumerateArray().First(x => x.GetProperty("parentTransactionId").ValueKind == JsonValueKind.String);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/transactions/{Id(child)}", new { amount = 10, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z" })).StatusCode);
        await Post(c, $"/api/bookkeeping/accounts/{account}/reconcile", new { statementDate = "2026-04-30", statementBalance = 70, transactionIds = new[] { Id(entry) } });
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/transactions/{Id(entry)}", new { amount = 30, type = "Expense", occurredOnUtc = "2026-04-10T12:00:00Z", updateMetadata = true, tags = "new" })).StatusCode);
    }

    [Fact]
    public async Task PayeeMerge_RepairsReferencesAndDeleteRequiresUnusedRecord()
    {
        using var c = await Client(); using var other = await Client(); var account = Id(await Account(c)); var entry = await Entry(c, account);
        var source = Id(await Post(c, "/api/bookkeeping/payees", new { name = "Shop old" })); var target = Id(await Post(c, "/api/bookkeeping/payees", new { name = "Shop" }));
        (await c.PutAsJsonAsync($"/api/bookkeeping/entries/{Id(entry)}", new { payeeId = source, tags = "home", status = "Cleared" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/bookkeeping/payees/{source}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/bookkeeping/payees/{source}/merge", new { targetId = target })).StatusCode);
        await Post(c, $"/api/bookkeeping/payees/{source}/merge", new { targetId = target });
        var metadata = (await c.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/entries/{Id(entry)}/details")).GetProperty("metadata"); Assert.Equal(target, metadata.GetProperty("payeeId").GetGuid()); Assert.Equal("Cleared", metadata.GetProperty("status").GetString());
        (await c.PutAsJsonAsync($"/api/bookkeeping/payees/{target}", new { name = "Market new" })).EnsureSuccessStatusCode();
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export"); (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode(); Assert.Single((await c.GetFromJsonAsync<JsonElement>("/api/bookkeeping/payees")).EnumerateArray());
    }

    [Fact]
    public async Task Tags_RenameExactTokensMergeAndRemoveWithoutTouchingOtherUser()
    {
        using var c = await Client(); using var other = await Client(); var entry = await Entry(c, Id(await Account(c)));
        (await c.PutAsJsonAsync($"/api/bookkeeping/entries/{Id(entry)}", new { tags = "home, homework, HOME, shopping" })).EnsureSuccessStatusCode();
        await Post(c, "/api/bookkeeping/tags/rename", new { name = "home", newName = "shopping" });
        var tags = await c.GetFromJsonAsync<string[]>("/api/bookkeeping/tags"); Assert.Equal(new[] { "homework", "shopping" }, tags);
        await Post(other, "/api/bookkeeping/tags/rename", new { name = "shopping", newName = "foreign" });
        Assert.Equal(tags, await c.GetFromJsonAsync<string[]>("/api/bookkeeping/tags"));
        await Post(c, "/api/bookkeeping/tags/rename", new { name = "shopping", newName = "" }); Assert.Equal(new[] { "homework" }, await c.GetFromJsonAsync<string[]>("/api/bookkeeping/tags"));
    }

    [Fact]
    public async Task NamedViews_AreOwnedAndSurviveBackupAndRejectExternalUrls()
    {
        using var c = await Client(); using var other = await Client(); var view = await Post(c, "/api/bookkeeping/views", new { name = "Spending", query = "?type=Expense&q=Market" });
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/bookkeeping/views")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/bookkeeping/views/{Id(view)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/bookkeeping/views", new { name = "Bad", query = "https://example.com" })).StatusCode);
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export"); Assert.Equal(6, backup.GetProperty("version").GetInt32()); (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        Assert.Equal("?type=Expense&q=Market", (await c.GetFromJsonAsync<JsonElement>("/api/bookkeeping/views"))[0].GetProperty("query").GetString());
    }

    [Fact]
    public async Task CategoryHierarchy_RejectsCyclesAndForeignParentsAndSurvivesRestore()
    {
        using var c = await Client(); using var other = await Client(); var a = Id(await Category(c, "Parent")); var b = Id(await Category(c, "Child")); var foreign = Id(await Category(other, "Other parent"));
        (await c.PutAsJsonAsync($"/api/bookkeeping/categories/{b}/parent", new { parentId = a })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/bookkeeping/categories/{a}/parent", new { parentId = b })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/bookkeeping/categories/{a}/parent", new { parentId = foreign })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/categories/{a}")).StatusCode);
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export"); (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        Assert.Equal(a, (await c.GetFromJsonAsync<JsonElement>($"/api/categories/{b}")).GetProperty("parentCategoryId").GetGuid());
    }

    [Fact]
    public async Task CategoryMerge_RepairsEntriesDefaultsRulesFiltersAndChildren()
    {
        using var c = await Client(); var account = Id(await Account(c)); var source = Id(await Category(c, "Old")); var target = Id(await Category(c, "New")); var child = Id(await Category(c, "Child"));
        var entry = await Entry(c, account, source); await Post(c, "/api/bookkeeping/payees", new { name = "Shop", defaultCategoryId = source });
        await Post(c, "/api/import-rules", new { name = "Shop", matchField = "Note", matchOperator = "Contains", matchValue = "Market", assignCategoryId = source, assignTransactionType = "Expense", priority = 100, isActive = true });
        await Post(c, "/api/bookkeeping/views", new { name = "Old category", query = $"?categoryId={source}&q=Market" });
        (await c.PutAsJsonAsync($"/api/bookkeeping/categories/{child}/parent", new { parentId = source })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/bookkeeping/categories/{source}/merge", new { targetId = child })).StatusCode);
        await Post(c, $"/api/bookkeeping/categories/{source}/merge", new { targetId = target });
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/categories/{source}")).StatusCode);
        Assert.Equal(target, (await c.GetFromJsonAsync<JsonElement>($"/api/transactions/{Id(entry)}")).GetProperty("categoryId").GetGuid());
        Assert.Equal(target, (await c.GetFromJsonAsync<JsonElement>("/api/bookkeeping/payees"))[0].GetProperty("defaultCategoryId").GetGuid());
        Assert.Equal(target, (await c.GetFromJsonAsync<JsonElement>("/api/import-rules"))[0].GetProperty("assignCategoryId").GetGuid());
        Assert.Contains(target.ToString(), (await c.GetFromJsonAsync<JsonElement>("/api/bookkeeping/views"))[0].GetProperty("query").GetString());
        Assert.Equal(target, (await c.GetFromJsonAsync<JsonElement>($"/api/categories/{child}")).GetProperty("parentCategoryId").GetGuid());
    }

    [Fact]
    public async Task ExistingRulePreview_RechecksChangesPreservesIdentityAndExcludesLockedEntries()
    {
        using var c = await Client(); using var other = await Client(); var account = Id(await Account(c)); var category = Id(await Category(c, "Food")); var entry = await Entry(c, account); var refund = await Entry(c, account, null, "Market refund", "Refund");
        await Post(c, "/api/import-rules", new { name = "Market", matchField = "Note", matchOperator = "Contains", matchValue = "Market", assignCategoryId = category, assignTransactionType = "Expense", priority = 100, isActive = true });
        var preview = await Post(c, "/api/bookkeeping/rules/preview", new { }); Assert.Equal(2, preview.GetArrayLength()); Assert.Equal("Refund", preview.EnumerateArray().Single(x => Id(x) == Id(refund)).GetProperty("newType").GetString());
        var selected = preview.EnumerateArray().Select(x => new { id = Id(x), fingerprint = x.GetProperty("fingerprint").GetString() }).ToArray();
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync("/api/bookkeeping/rules/apply", new { selected })).StatusCode);
        await Post(c, $"/api/bookkeeping/accounts/{account}/reconcile", new { statementDate = "2026-04-30", statementBalance = 70, transactionIds = new[] { Id(entry) } });
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/bookkeeping/rules/apply", new { selected })).StatusCode);
        preview = await Post(c, "/api/bookkeeping/rules/preview", new { }); Assert.Single(preview.EnumerateArray());
        await Post(c, "/api/bookkeeping/rules/apply", new { selected = preview.EnumerateArray().Select(x => new { id = Id(x), fingerprint = x.GetProperty("fingerprint").GetString() }).ToArray() });
        Assert.Equal(category, (await c.GetFromJsonAsync<JsonElement>($"/api/transactions/{Id(refund)}")).GetProperty("categoryId").GetGuid());
        Assert.Empty((await Post(c, "/api/bookkeeping/rules/preview", new { })).EnumerateArray());
    }
    [Fact]
    public async Task CompoundRules_RequireEveryConditionAndSurviveBackup()
    {
        using var c = await Client(); var account = Id(await Account(c)); var otherAccount = Id(await Account(c)); var category = Id(await Category(c, "Food"));
        var wanted = await Entry(c, account); await Entry(c, otherAccount);
        await Post(c, "/api/import-rules", new { name = "Only this bank", matchField = "Note", matchOperator = "Contains", matchValue = "Market", assignCategoryId = category, assignTransactionType = "Expense", conditions = new[] { new { field = "Account", @operator = "Equals", value = account.ToString() }, new { field = "Amount", @operator = "GreaterThan", value = "20" } } });
        var preview = await Post(c, "/api/bookkeeping/rules/preview", new { }); Assert.Single(preview.EnumerateArray()); Assert.Equal(Id(wanted), Id(preview[0]));
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export"); (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        var rules = await c.GetFromJsonAsync<JsonElement>("/api/import-rules"); Assert.Equal(2, rules[0].GetProperty("conditions").GetArrayLength());
        Assert.Single((await Post(c, "/api/bookkeeping/rules/preview", new { })).EnumerateArray());
        var ruleId = Id(rules[0]);
        (await c.PutAsJsonAsync($"/api/import-rules/{ruleId}", new { name = "Changed", matchField = "Note", matchOperator = "Contains", matchValue = "Market", assignCategoryId = category, assignTransactionType = "Refund", conditions = Array.Empty<object>() })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/bookkeeping/rules/apply", new { selected = new[] { new { id = Id(wanted), fingerprint = preview[0].GetProperty("fingerprint").GetString() } } })).StatusCode);
    }

    [Fact]
    public async Task CategoryMerge_RejectsOverlappingBudgetsWithoutChangingEntries()
    {
        using var c = await Client(); var source = Id(await Category(c, "Source budget")); var target = Id(await Category(c, "Target budget")); var account = Id(await Account(c)); var entry = await Entry(c, account, source);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LedgerraDbContext>();
            var owner = (await db.Categories.SingleAsync(x => x.Id == source)).UserId;
            var period = new Ledgerra.Domain.Budgets.BudgetPeriod { Id = Guid.NewGuid(), UserId = owner, Year = 2026, Month = 4 };
            db.BudgetPeriods.Add(period);
            db.BudgetCategoryLimits.AddRange(new Ledgerra.Domain.Budgets.BudgetCategoryLimit { Id = Guid.NewGuid(), BudgetPeriodId = period.Id, CategoryId = source, PlannedAmount = 50 }, new Ledgerra.Domain.Budgets.BudgetCategoryLimit { Id = Guid.NewGuid(), BudgetPeriodId = period.Id, CategoryId = target, PlannedAmount = 70 });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/bookkeeping/categories/{source}/merge", new { targetId = target })).StatusCode);
        Assert.Equal(source, (await c.GetFromJsonAsync<JsonElement>($"/api/transactions/{Id(entry)}")).GetProperty("categoryId").GetGuid());
    }

}
