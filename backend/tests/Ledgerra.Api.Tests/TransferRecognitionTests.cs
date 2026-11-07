using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerra.Api.Tests;

public sealed class TransferRecognitionTests : IClassFixture<LedgerraApiFactory>
{
    private readonly LedgerraApiFactory _factory;
    public TransferRecognitionTests(LedgerraApiFactory factory) => _factory = factory;
    private async Task<HttpClient> Client()
    {
        var client = _factory.CreateClient();
        var r = await client.PostAsJsonAsync("/api/auth/register", new { login = "match-" + Guid.NewGuid().ToString("N")[..12], password = "P@ssw0rd123!" }); r.EnsureSuccessStatusCode();
        var auth = await r.Content.ReadFromJsonAsync<JsonElement>(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString()); return client;
    }
    private static async Task<Guid> Account(HttpClient client, string name, string currency = "USD")
    {
        var r = await client.PostAsJsonAsync("/api/accounts", new { name, type = "Checking", currencyCode = currency }); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<JsonElement> Entry(HttpClient client, Guid account, string type, string note, int day = 10, decimal amount = 25)
    {
        var r = await client.PostAsJsonAsync("/api/transactions", new { accountId = account, type, amount, note, occurredOnUtc = $"2026-04-{day:D2}T12:00:00Z" }); r.EnsureSuccessStatusCode(); return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task LaterCounterpart_AutomaticallyMatchesAndUndoPreservesBothEntries()
    {
        using var client = await Client(); var bank = await Account(client, "Bank"); var savings = await Account(client, "Savings");
        var outgoing = await Entry(client, bank, "Expense", "Transfer to savings");
        var incoming = await Entry(client, savings, "Income", "Transfer from bank", 12);
        Assert.Equal("TransferIn", incoming.GetProperty("type").GetString());
        var matches = await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/recognized"); var history = Assert.Single(matches.EnumerateArray()); Assert.True(history.GetProperty("automatic").GetBoolean());
        var group = history.GetProperty("id").GetGuid();
        var backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export");
        (await client.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/recognized")).EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/transactions/{outgoing.GetProperty("id").GetGuid()}")).StatusCode);
        (await client.PostAsync($"/api/bookkeeping/transfers/{group}/unlink", null)).EnsureSuccessStatusCode();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/transactions"); Assert.Equal(2, list.GetArrayLength());
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("type").GetString() == "Income"); Assert.Contains(list.EnumerateArray(), t => t.GetProperty("type").GetString() == "Expense");
        var b = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{bank}/register"); var s = await client.GetFromJsonAsync<JsonElement>($"/api/bookkeeping/accounts/{savings}/register"); Assert.Equal(-25, b.GetProperty("balance").GetDecimal()); Assert.Equal(25, s.GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task AmountAndDateAlone_AreSuggestionsAndNeverAutomatic()
    {
        using var client = await Client(); var a = await Account(client, "A"); var b = await Account(client, "B");
        await Entry(client, a, "Expense", "Groceries"); await Entry(client, b, "Income", "Salary");
        var candidates = await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/candidates"); Assert.False(Assert.Single(candidates.EnumerateArray()).GetProperty("canAutoLink").GetBoolean());
        var scan = await client.PostAsync("/api/bookkeeping/transfers/recognize", null); scan.EnsureSuccessStatusCode(); Assert.Equal(0, (await scan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("linked").GetInt32());
    }

    [Fact]
    public async Task RescanMatchesExistingEntriesAndIsIdempotent()
    {
        using var client = await Client(); var a = await Account(client, "Checking account"); var b = await Account(client, "Savings account");
        // Initially outside the automatic three-day gap; an explicit wider scan can recognize them later.
        await Entry(client, a, "Expense", "Internal transfer to Savings account", 10); await Entry(client, b, "Income", "Internal transfer from Checking account", 15);
        var r = await client.PostAsync("/api/bookkeeping/transfers/recognize?dayWindow=5", null); r.EnsureSuccessStatusCode(); Assert.Equal(1, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("linked").GetInt32());
        var again = await client.PostAsync("/api/bookkeeping/transfers/recognize?dayWindow=5", null); Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("linked").GetInt32());
    }

    [Fact]
    public async Task AmbiguousPairsAreNotChosenArbitrarily()
    {
        using var client = await Client(); var a = await Account(client, "A"); var b = await Account(client, "B"); var c = await Account(client, "C");
        await Entry(client, a, "Expense", "Debit one"); await Entry(client, a, "Expense", "Debit two");
        await Entry(client, b, "Income", "Credit one"); await Entry(client, c, "Income", "Credit two");
        var candidates = await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/candidates"); Assert.Equal(4, candidates.GetArrayLength()); Assert.All(candidates.EnumerateArray(), x => Assert.False(x.GetProperty("canAutoLink").GetBoolean()));
    }

    [Fact]
    public async Task ClearingUserDataRemovesMatchingAndBookkeepingRecordsOnlyForThatUser()
    {
        using var client = await Client(); using var other = await Client(); var a = await Account(client, "Checking"); var b = await Account(client, "Savings");
        await Entry(client, a, "Expense", "Transfer to Savings"); await Entry(client, b, "Income", "Transfer from Checking");
        (await client.PostAsJsonAsync("/api/bookkeeping/payees", new { name = "Own payee" })).EnsureSuccessStatusCode();
        (await other.PostAsJsonAsync("/api/bookkeeping/payees", new { name = "Other payee" })).EnsureSuccessStatusCode();
        (await client.DeleteAsync("/api/settings/account-data")).EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/recognized")).EnumerateArray());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/payees")).EnumerateArray());
        Assert.Single((await other.GetFromJsonAsync<JsonElement>("/api/bookkeeping/payees")).EnumerateArray());
    }

    [Fact]
    public async Task GenericTransferWordingDoesNotProveTheSameCounterparty()
    {
        using var client = await Client(); var a = await Account(client, "Personal bank"); var b = await Account(client, "Other bank");
        await Entry(client, a, "Expense", "Transfer to mortgage company"); await Entry(client, b, "Income", "Transfer from employer");
        var candidates = await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/candidates");
        Assert.False(Assert.Single(candidates.EnumerateArray()).GetProperty("canAutoLink").GetBoolean());
    }

    [Fact]
    public async Task ReconciledForeignCurrencyAndOtherUsersAreExcluded()
    {
        using var client = await Client(); using var other = await Client();
        var a = await Account(client, "A"); var b = await Account(client, "B", "EUR"); var c = await Account(other, "C");
        var entry = await Entry(client, a, "Expense", "Transfer out"); await Entry(client, b, "Income", "Transfer in"); await Entry(other, c, "Income", "Transfer in");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/candidates")).EnumerateArray());
        (await client.PostAsJsonAsync($"/api/bookkeeping/accounts/{a}/reconcile", new { statementDate = "2026-04-30", statementBalance = -25, transactionIds = new[] { entry.GetProperty("id").GetGuid() } })).EnsureSuccessStatusCode();
        var d = await Account(client, "D"); await Entry(client, d, "Income", "Transfer in");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/bookkeeping/transfers/candidates")).EnumerateArray());
    }
}
