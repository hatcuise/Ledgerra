using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ledgerra.Api.Services.Imports;

namespace Ledgerra.Api.Tests;

public sealed class BookkeepingWorkflowTests : IClassFixture<LedgerraApiFactory>
{
    private readonly LedgerraApiFactory _factory;
    public BookkeepingWorkflowTests(LedgerraApiFactory factory) => _factory = factory;
    private async Task<HttpClient> Client()
    {
        var client = _factory.CreateClient();
        var result = await client.PostAsJsonAsync("/api/auth/register", new { login = "books-" + Guid.NewGuid().ToString("N")[..12], password = "P@ssw0rd123!" });
        result.EnsureSuccessStatusCode();
        var auth = await result.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString());
        return client;
    }
    private static async Task<Guid> Account(HttpClient client, decimal opening = 0)
    {
        var r = await client.PostAsJsonAsync("/api/accounts", new { name = "Books", type = "Checking", currencyCode = "USD", openingBalance = opening });
        r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<JsonElement> Entry(HttpClient client, Guid account, string type, decimal amount)
    {
        var r = await client.PostAsJsonAsync("/api/transactions", new { accountId = account, type, amount, occurredOnUtc = "2026-04-10T12:00:00Z", note = "Bookkeeping test" });
        r.EnsureSuccessStatusCode(); return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Refund_ReducesExpensesAndIncreasesBalanceWithoutIncome()
    {
        using var client = await Client(); var account = await Account(client, 100);
        await Entry(client, account, "Expense", 50); await Entry(client, account, "Refund", 20);
        var register = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{account}/register");
        Assert.Equal(70, register.GetProperty("balance").GetDecimal());
        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/summary?month=2026-04");
        Assert.Equal(0, dashboard.GetProperty("income").GetDecimal());
        Assert.Equal(30, dashboard.GetProperty("expenses").GetDecimal());
    }

    [Fact]
    public async Task Reconciliation_RequiresExactBalanceLocksEntriesAndCanBeReopened()
    {
        using var client = await Client(); var account = await Account(client, 100);
        var entry = await Entry(client, account, "Expense", 30); var id = entry.GetProperty("id").GetGuid();
        var wrong = await client.PostAsJsonAsync($"/api/bookkeeping/accounts/{account}/reconcile", new { statementDate = "2026-04-30", statementBalance = 90, transactionIds = new[] { id } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var r = await client.PostAsJsonAsync($"/api/bookkeeping/accounts/{account}/reconcile", new { statementDate = "2026-04-30", statementBalance = 70, transactionIds = new[] { id } });
        r.EnsureSuccessStatusCode(); var statement = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/transactions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/bookkeeping/entries/{id}", new { status = "Cleared" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/accounts/{account}", new { name = "Books", type = "Checking", currencyCode = "USD", openingBalance = 200 })).StatusCode);
        (await client.PostAsync($"/api/bookkeeping/reconciliations/{statement.GetProperty("id").GetGuid()}/reopen", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/transactions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Reconciliation_IsUserScopedAndRejectsFutureEntries()
    {
        using var client = await Client(); using var other = await Client(); var account = await Account(client);
        var entry = await Entry(client, account, "Income", 25); var id = entry.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/bookkeeping/accounts/{account}/register")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/bookkeeping/accounts/{account}/reconcile", new { statementDate = "2026-04-01", statementBalance = 25, transactionIds = new[] { id } })).StatusCode);
    }

    [Fact]
    public async Task LinkedCardPayment_PreservesBalancesAndRemovesFalseIncomeAndExpense()
    {
        using var client = await Client(); var bank = await Account(client, 100); var card = await Account(client, -50);
        var outgoing = await Entry(client, bank, "Expense", 25); var incoming = await Entry(client, card, "Income", 25);
        (await client.PostAsJsonAsync("/api/bookkeeping/transfers/link", new { outgoingId = outgoing.GetProperty("id").GetGuid(), incomingId = incoming.GetProperty("id").GetGuid() })).EnsureSuccessStatusCode();
        var b = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{bank}/register");
        var c = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{card}/register");
        Assert.Equal(75, b.GetProperty("balance").GetDecimal()); Assert.Equal(-25, c.GetProperty("balance").GetDecimal());
        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/summary?month=2026-04");
        Assert.Equal(0, dashboard.GetProperty("income").GetDecimal()); Assert.Equal(0, dashboard.GetProperty("expenses").GetDecimal());
    }

    [Fact]
    public async Task ProfilesAndPayees_AreSavedPerUserAndIncludedInBackup()
    {
        using var client = await Client(); using var other = await Client();
        (await client.PostAsJsonAsync("/api/bookkeeping/profiles", new { name = "Card bank", settings = new { dateColumn = "date", amountColumn = "amount", options = new { positiveType = "Expense", negativeType = "Refund" } } })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/bookkeeping/payees", new { name = "Market" })).EnsureSuccessStatusCode();
        var profiles = await other.GetFromJsonAsync<JsonElement>("/api/bookkeeping/profiles"); Assert.Empty(profiles.EnumerateArray());
        var backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export");
        Assert.Equal(6, backup.GetProperty("version").GetInt32()); Assert.Single(backup.GetProperty("importProfiles").EnumerateArray()); Assert.Single(backup.GetProperty("payees").EnumerateArray());
        (await client.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/profiles")).EnumerateArray());
    }

    [Fact]
    public async Task CsvPreview_AppliesSavedConventionsAndMarksDuplicatesWithoutAi()
    {
        using var client = await Client(); var account = await Account(client);
        async Task<JsonElement> Preview()
        {
            using var form = new MultipartFormDataContent();
            foreach (var (key, value) in new Dictionary<string, string> { ["accountId"] = account.ToString(), ["dateColumn"] = "date", ["amountColumn"] = "amount", ["descriptionColumn"] = "memo", ["delimiter"] = ";", ["cultureName"] = "de-DE", ["dateFormat"] = "dd.MM.yyyy", ["positiveType"] = "Expense", ["negativeType"] = "Refund" }) form.Add(new StringContent(value), key);
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("date;amount;memo\n10.04.2026;1.234,56;Market\n11.04.2026;-25,00;Refund\n")); file.Headers.ContentType = new MediaTypeHeaderValue("text/csv"); form.Add(file, "file", "bank.csv");
            var response = await client.PostAsync("/api/imports/monthly-report/csv-preview", form); response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        var preview = await Preview(); var drafts = preview.GetProperty("transactions");
        Assert.Equal("Expense", drafts[0].GetProperty("type").GetString()); Assert.Equal(1234.56m, drafts[0].GetProperty("amount").GetDecimal()); Assert.Equal("Refund", drafts[1].GetProperty("type").GetString());
        (await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new { transactions = drafts })).EnsureSuccessStatusCode();
        var again = (await Preview()).GetProperty("transactions"); Assert.All(again.EnumerateArray(), x => Assert.True(x.GetProperty("isLikelyDuplicate").GetBoolean()));
    }

    [Fact]
    public async Task TransfersRejectMismatchedCurrencies()
    {
        using var client = await Client(); var bank = await Account(client);
        var euroResponse = await client.PostAsJsonAsync("/api/accounts", new { name = "Euro", type = "Checking", currencyCode = "EUR" });
        euroResponse.EnsureSuccessStatusCode(); var euro = (await euroResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var result = await client.PostAsJsonAsync("/api/transactions", new { accountId = bank, destinationAccountId = euro, type = "Transfer", amount = 25, occurredOnUtc = "2026-04-10T12:00:00Z" });
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task ScheduledTransfer_CreatesPairedEntriesOnlyOnce()
    {
        using var client = await Client(); var bank = await Account(client, 100); var card = await Account(client, -25);
        var schedule = await client.PostAsJsonAsync("/api/recurring-transactions", new { accountId = bank, destinationAccountId = card, amount = 25, type = "Transfer", interval = "Monthly", startOnUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O"), note = "Scheduled card payment" });
        schedule.EnsureSuccessStatusCode();
        (await client.PostAsync("/api/recurring-transactions/generate", null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/recurring-transactions/generate", null)).EnsureSuccessStatusCode();
        var b = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{bank}/register");
        var c = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{card}/register");
        Assert.Equal(75, b.GetProperty("balance").GetDecimal()); Assert.Equal(0, c.GetProperty("balance").GetDecimal());
        Assert.Single(b.GetProperty("entries").EnumerateArray()); Assert.Single(c.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task BulkCorrection_ChangesExplicitTypeAndRejectsForeignEntriesAtomically()
    {
        using var client = await Client(); using var other = await Client();
        var account = await Account(client); var own = await Entry(client, account, "Income", 25);
        var foreign = await Entry(other, await Account(other), "Income", 25);
        var id = own.GetProperty("id").GetGuid();
        var rejected = await client.PutAsJsonAsync("/api/bookkeeping/entries/bulk", new { ids = new[] { id, foreign.GetProperty("id").GetGuid() }, type = "Expense" });
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        var unchanged = await client.GetFromJsonAsync<JsonElement>($"/api/transactions/{id}"); Assert.Equal("Income", unchanged.GetProperty("type").GetString());
        (await client.PutAsJsonAsync("/api/bookkeeping/entries/bulk", new { ids = new[] { id }, type = "Refund" })).EnsureSuccessStatusCode();
        var updated = await client.GetFromJsonAsync<JsonElement>($"/api/transactions/{id}"); Assert.Equal("Refund", updated.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Backup_RoundTripsReconciledRegisterAndPayee()
    {
        using var client = await Client(); var account = await Account(client, 100); var entry = await Entry(client, account, "Expense", 25); var id = entry.GetProperty("id").GetGuid();
        var p = await client.PostAsJsonAsync("/api/bookkeeping/payees", new { name = "Store" }); p.EnsureSuccessStatusCode(); var payee = (await p.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PutAsJsonAsync($"/api/bookkeeping/entries/{id}", new { payeeId = payee, status = "Cleared", reference = "123", tags = "household" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/bookkeeping/accounts/{account}/reconcile", new { statementDate = "2026-04-30", statementBalance = 75, transactionIds = new[] { id } })).EnsureSuccessStatusCode();
        var backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export");
        (await client.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        var register = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{account}/register");
        var metadata = register.GetProperty("entries")[0].GetProperty("metadata"); Assert.Equal("Reconciled", metadata.GetProperty("status").GetString()); Assert.Equal(payee, metadata.GetProperty("payeeId").GetGuid()); Assert.Equal("household", metadata.GetProperty("tags").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/transactions/{id}")).StatusCode);
    }

    [Fact]
    public async Task CsvPreviewAndCommit_PreservePayeeCategoryReferenceAndTags()
    {
        using var client = await Client(); var account = await Account(client);
        var categoryResponse = await client.PostAsJsonAsync("/api/categories", new { name = "CSV services", kind = "Expense" }); categoryResponse.EnsureSuccessStatusCode();
        var category = (await categoryResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var form = new MultipartFormDataContent();
        foreach (var (key, value) in new Dictionary<string, string> { ["accountId"] = account.ToString(), ["dateColumn"] = "date", ["amountColumn"] = "amount", ["payeeColumn"] = "payee", ["categoryColumn"] = "category", ["referenceColumn"] = "number", ["tagsColumn"] = "tags" }) form.Add(new StringContent(value), key);
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("date,amount,payee,category,number,tags\n4/10/2026,-133,Merchant,CSV services,123,household\n")); file.Headers.ContentType = new MediaTypeHeaderValue("text/csv"); form.Add(file, "file", "bank.csv");
        var preview = await client.PostAsync("/api/imports/monthly-report/csv-preview", form); preview.EnsureSuccessStatusCode(); var drafts = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactions");
        Assert.Equal(category, drafts[0].GetProperty("categoryId").GetGuid()); Assert.Equal("Merchant", drafts[0].GetProperty("payeeName").GetString());
        (await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new { transactions = drafts })).EnsureSuccessStatusCode();
        var register = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{account}/register"); var metadata = register.GetProperty("entries")[0].GetProperty("metadata");
        Assert.Equal("123", metadata.GetProperty("reference").GetString()); Assert.Equal("household", metadata.GetProperty("tags").GetString()); Assert.NotEqual(Guid.Empty, metadata.GetProperty("payeeId").GetGuid());
        var payees = await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/payees"); Assert.Equal("Merchant", Assert.Single(payees.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task MerchantExpenseRule_DoesNotTurnRefundsIntoSpending()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Ledgerra.Infrastructure.Persistence.LedgerraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new Ledgerra.Infrastructure.Persistence.LedgerraDbContext(options);
        var user = Guid.NewGuid(); var category = Guid.NewGuid();
        db.CategorizationRules.Add(new Ledgerra.Domain.Imports.CategorizationRule { Id = Guid.NewGuid(), UserId = user, Name = "Market", MatchValue = "Market", AssignCategoryId = category, AssignTransactionType = Ledgerra.Domain.Transactions.TransactionType.Expense });
        await db.SaveChangesAsync();
        var draft = ImportDraftReviewItem.FromAnalyzedDraft("refund", Guid.NewGuid(), null, 20, "Refund", DateTime.UtcNow, "Market return", 1, []);
        var result = await new ImportCategorizationRuleMatcher(db).ApplyAsync(user, new[] { draft }, CancellationToken.None);
        Assert.Equal("Refund", result[0].Type); Assert.Equal(category, result[0].CategoryId);
    }

    [Fact]
    public void SeparateDebitCreditColumns_AndExplicitTypesAreRespected()
    {
        var mapper = new CsvBankImportMapper();
        var result = mapper.Map("date,debit,credit,type\n2026-04-10,50,,Expense\n2026-04-11,,20,Refund\n", Guid.NewGuid(), "date", "", null, false, new CsvImportOptions { DebitColumn = "debit", CreditColumn = "credit", TypeColumn = "type" });
        Assert.Equal("Expense", result[0].Type); Assert.Equal("Refund", result[1].Type);
        Assert.Throws<InvalidOperationException>(() => mapper.Map("date,debit,credit\n2026-04-10,50,20\n", Guid.NewGuid(), "date", "", null, false, new CsvImportOptions { DebitColumn = "debit", CreditColumn = "credit" }));
    }
}
