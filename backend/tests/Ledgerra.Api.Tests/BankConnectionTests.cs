using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerra.Api.Services.Banking;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledgerra.Api.Tests;

public sealed class BankConnectionTests(LedgerraApiFactory factory) : IClassFixture<LedgerraApiFactory>
{
    private sealed class FakePlaid : IPlaidClient
    {
        public List<(string Path, Dictionary<string, object?> Payload)> Calls { get; } = [];
        public Queue<object> Pages { get; } = new();
        public bool FailRemove { get; set; }
        public Task<JsonElement> SendAsync(BankProviderSettings settings, string secret, string path, Dictionary<string, object?> body, CancellationToken ct)
        {
            Assert.Equal("test-secret", secret); Calls.Add((path, body));
            object response = path switch
            {
                "/link/token/create" => new { link_token = "link-sandbox-test-token" },
                "/item/public_token/exchange" => new { access_token = "access-sandbox-test-private", item_id = "item-" + settings.ClientId },
                "/accounts/get" => new { accounts = new[] { new { account_id = "bank-account", name = "Test bank checking", mask = "1234", type = "depository", balances = new { iso_currency_code = "USD", current = 999 } }, new { account_id = "bank-card", name = "Test credit card", mask = "5678", type = "credit", balances = new { iso_currency_code = "USD", current = 50 } } } },
                "/transactions/sync" => Pages.Count > 0 ? Pages.Dequeue() : Page([], [], [], "cursor-last"),
                "/item/remove" => FailRemove ? new BankProviderException("INTERNAL_SERVER_ERROR") : new { removed = true },
                _ => throw new Exception("Unexpected operation")
            };
            if (response is Exception e) throw e;
            return Task.FromResult(JsonSerializer.SerializeToElement(response));
        }
    }
    private static object Page(object[] added, object[] modified, object[] removed, string cursor, bool more = false) => new { added, modified, removed, next_cursor = cursor, has_more = more };
    private static object Row(string id, decimal amount = 30, bool pending = false, string name = "Market", string date = "2026-04-10") => new { transaction_id = id, account_id = "bank-account", amount, pending, name, merchant_name = "Market", date, iso_currency_code = "USD" };
    private WebApplicationFactory<Program> Host(FakePlaid fake) => factory.WithWebHostBuilder(builder => builder.ConfigureServices(s => { s.RemoveAll<IPlaidClient>(); s.AddSingleton<IPlaidClient>(fake); }));
    private static async Task<HttpClient> Client(WebApplicationFactory<Program> host)
    {
        var c = host.CreateClient(); var r = await c.PostAsJsonAsync("/api/auth/register", new { login = "bank-" + Guid.NewGuid().ToString("N")[..12], password = "P@ssw0rd123!" }); r.EnsureSuccessStatusCode();
        var auth = await r.Content.ReadFromJsonAsync<JsonElement>(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString()); return c;
    }
    private static async Task<JsonElement> Post(HttpClient c, string path, object data)
    { var r = await c.PostAsJsonAsync(path, data); r.EnsureSuccessStatusCode(); return await r.Content.ReadFromJsonAsync<JsonElement>(); }
    private static async Task Configure(HttpClient c, string country = "US") => (await c.PutAsJsonAsync("/api/banks/settings", new { clientId = "client-" + Guid.NewGuid().ToString("N"), secret = "test-secret", country, environment = "Sandbox", redirectUri = "https://ledgerra.example/imports" })).EnsureSuccessStatusCode();
    private static Guid Id(JsonElement row) => row.GetProperty("id").GetGuid();
    private static Task<JsonElement> Account(HttpClient c, string currency = "USD") => Post(c, "/api/accounts", new { name = "Linked checking", type = "Checking", currencyCode = currency, openingBalance = 100 });
    private static async Task<(Guid Connection, Guid Source, Guid Ledger)> Linked(HttpClient c)
    {
        await Configure(c); var connection = Id(await Post(c, "/api/banks/exchange", new { publicToken = "public-sandbox-test", name = "Bank" }));
        var sources = await c.GetFromJsonAsync<JsonElement>("/api/banks/connections"); var source = Id(sources[0].GetProperty("accounts").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Test bank checking")); var ledger = Id(await Account(c));
        (await c.PutAsJsonAsync($"/api/banks/accounts/{source}/mapping", new { accountId = ledger })).EnsureSuccessStatusCode(); return (connection, source, ledger);
    }

    [Fact]
    public async Task SettingsAndLink_ProtectSecretsUseCountryAndRejectUnsafeRedirects()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); await Configure(c, "CA");
        var settings = await c.GetFromJsonAsync<JsonElement>("/api/banks/settings"); Assert.True(settings.GetProperty("isConfigured").GetBoolean()); Assert.DoesNotContain("test-secret", settings.ToString());
        var link = await Post(c, "/api/banks/link-token", new { }); Assert.Equal("link-sandbox-test-token", link.GetProperty("linkToken").GetString());
        var payload = JsonSerializer.SerializeToElement(fake.Calls.Last().Payload); Assert.Equal("CA", payload.GetProperty("country_codes")[0].GetString()); Assert.Equal("transactions", payload.GetProperty("products")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/banks/settings", new { clientId = "bad", secret = "test-secret", redirectUri = "http://not-secure/imports" })).StatusCode);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<LedgerraDbContext>();
        Assert.DoesNotContain("test-secret", (await db.BankProviderSettings.SingleAsync(x => x.ClientId == settings.GetProperty("clientId").GetString())).EncryptedSecret);
    }

    [Fact]
    public async Task Connections_AreOwnedMappingChecksCurrencyAndBackupExcludesBankSecrets()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); using var other = await Client(host); var (connection, source, ledger) = await Linked(c);
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/banks/connections")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/banks/connections/{connection}/sync", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/banks/accounts/{source}/mapping", new { accountId = ledger })).StatusCode);
        var foreign = Id(await Account(other)); Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/banks/accounts/{source}/mapping", new { accountId = foreign })).StatusCode);
        var euro = Id(await Account(c, "EUR")); Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/banks/accounts/{source}/mapping", new { accountId = euro })).StatusCode);
        var connections = await c.GetFromJsonAsync<JsonElement>("/api/banks/connections"); Assert.DoesNotContain("access-sandbox", connections.ToString()); Assert.DoesNotContain("encrypted", connections.ToString().ToLowerInvariant());
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export"); Assert.DoesNotContain("BankConnection", backup.ToString()); Assert.DoesNotContain("test-secret", backup.ToString()); Assert.DoesNotContain("access-sandbox", backup.ToString());
        (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        connections = await c.GetFromJsonAsync<JsonElement>("/api/banks/connections"); Assert.Equal(JsonValueKind.Null, connections[0].GetProperty("accounts")[0].GetProperty("ledgerAccountId").ValueKind);
    }

    [Fact]
    public async Task Sync_StagesPostedEntriesSkipsPendingPreservesOpeningBalanceAndDeduplicatesImports()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); var (connection, _, ledger) = await Linked(c);
        fake.Pages.Enqueue(Page([Row("purchase"), Row("income", -60, false, "Salary"), Row("refund", -10, false, "Refund Market"), Row("pending", 20, true)], [], [], "cursor-1"));
        await Post(c, $"/api/banks/connections/{connection}/sync", new { });
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/transactions")).EnumerateArray());
        Assert.Equal(100, (await c.GetFromJsonAsync<JsonElement>($"/api/accounts/{ledger}")).GetProperty("currentBalance").GetDecimal());
        var preview = await Post(c, $"/api/banks/connections/{connection}/preview", new { }); var drafts = preview.GetProperty("transactions").EnumerateArray().ToArray();
        Assert.Equal(3, drafts.Length); Assert.All(drafts, d => Assert.False(d.GetProperty("isSelectedByDefault").GetBoolean()));
        Assert.Contains(drafts, d => d.GetProperty("type").GetString() == "Expense"); Assert.Contains(drafts, d => d.GetProperty("type").GetString() == "Refund"); Assert.Contains(drafts, d => d.GetProperty("type").GetString() == "Income");
        var purchase = drafts.Single(d => d.GetProperty("type").GetString() == "Expense");
        await Post(c, "/api/imports/monthly-report/commit", new { transactions = new[] { purchase } });
        preview = await Post(c, $"/api/banks/connections/{connection}/preview", new { }); Assert.Equal(2, preview.GetProperty("transactions").GetArrayLength());
        var response = await c.PostAsJsonAsync("/api/imports/monthly-report/commit", new { transactions = new[] { purchase } }); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(70, (await c.GetFromJsonAsync<JsonElement>($"/api/accounts/{ledger}")).GetProperty("currentBalance").GetDecimal());
    }

    [Fact]
    public async Task PaginationFailure_DoesNotAdvanceCursorOrPersistPartialTransactions()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); var (connection, _, _) = await Linked(c);
        fake.Pages.Enqueue(Page([Row("page-1")], [], [], "page-1-cursor", true)); fake.Pages.Enqueue(new BankProviderException("TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION"));
        Assert.Equal(HttpStatusCode.BadGateway, (await c.PostAsJsonAsync($"/api/banks/connections/{connection}/sync", new { })).StatusCode);
        using (var scope = host.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<LedgerraDbContext>(); Assert.Equal("", (await db.BankConnections.SingleAsync(x => x.Id == connection)).Cursor); Assert.Empty(await db.BankFeedEntries.Where(x => x.ConnectionId == connection).ToListAsync()); }
        fake.Pages.Enqueue(Page([Row("page-1")], [], [], "first", true)); fake.Pages.Enqueue(Page([Row("page-2")], [], [], "complete"));
        await Post(c, $"/api/banks/connections/{connection}/sync", new { });
        var preview = await Post(c, $"/api/banks/connections/{connection}/preview", new { }); Assert.Equal(2, preview.GetProperty("transactions").GetArrayLength());
        var syncCalls = fake.Calls.Where(x => x.Path == "/transactions/sync").ToList(); Assert.False(syncCalls[2].Payload.ContainsKey("cursor")); Assert.Equal("first", syncCalls[3].Payload["cursor"]);
    }

    [Fact]
    public async Task ModifiedAndRemovedBankEntries_DoNotRewriteImportedLedgerAndDisconnectPreservesIt()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); var (connection, _, ledger) = await Linked(c);
        fake.Pages.Enqueue(Page([Row("posted")], [], [], "one")); await Post(c, $"/api/banks/connections/{connection}/sync", new { });
        var preview = await Post(c, $"/api/banks/connections/{connection}/preview", new { }); await Post(c, "/api/imports/monthly-report/commit", new { transactions = preview.GetProperty("transactions") });
        fake.Pages.Enqueue(Page([], [Row("posted", 99)], [new { transaction_id = "posted" }], "two")); await Post(c, $"/api/banks/connections/{connection}/sync", new { });
        preview = await Post(c, $"/api/banks/connections/{connection}/preview", new { }); Assert.Empty(preview.GetProperty("transactions").EnumerateArray()); Assert.Contains("removed", preview.GetProperty("warnings").ToString());
        Assert.Equal(70, (await c.GetFromJsonAsync<JsonElement>($"/api/accounts/{ledger}")).GetProperty("currentBalance").GetDecimal());
        fake.FailRemove = true; Assert.Equal(HttpStatusCode.BadGateway, (await c.DeleteAsync($"/api/banks/connections/{connection}")).StatusCode); Assert.Single((await c.GetFromJsonAsync<JsonElement>("/api/banks/connections")).EnumerateArray());
        fake.FailRemove = false; (await c.DeleteAsync($"/api/banks/connections/{connection}")).EnsureSuccessStatusCode(); Assert.Single((await c.GetFromJsonAsync<JsonElement>("/api/transactions")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/banks/settings")).StatusCode);
    }

    [Fact]
    public async Task Reconnect_UsesUpdateModeAndEnvironmentCannotChangeWhileConnected()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); var (connection, _, _) = await Linked(c);
        await Post(c, "/api/banks/link-token", new { connectionId = connection }); var call = fake.Calls.Last(); Assert.True(call.Payload.ContainsKey("access_token")); Assert.False(call.Payload.ContainsKey("products"));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync("/api/banks/settings", new { clientId = "different", secret = "test-secret", environment = "Production" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync("/api/banks/settings")).StatusCode);
    }
    [Fact]
    public async Task InitialDataNotReady_AcceptsEmptyProviderCursorWithoutPosting()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); var (connection, _, _) = await Linked(c);
        fake.Pages.Enqueue(Page([], [], [], ""));
        var result = await Post(c, $"/api/banks/connections/{connection}/sync", new { }); Assert.True(result.GetProperty("initialDataPending").GetBoolean());
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/transactions")).EnumerateArray());
    }

    [Fact]
    public async Task DataReset_ClearsLocalBankCredentialsAndQueueOnlyForCurrentUser()
    {
        var fake = new FakePlaid(); using var host = Host(fake); using var c = await Client(host); using var other = await Client(host);
        var (connection, _, _) = await Linked(c); await Linked(other);
        fake.Pages.Enqueue(Page([Row("reset-feed")], [], [], "reset")); await Post(c, $"/api/banks/connections/{connection}/sync", new { });
        (await c.DeleteAsync("/api/settings/account-data")).EnsureSuccessStatusCode();
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/banks/connections")).EnumerateArray()); Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/banks/settings")).GetProperty("isConfigured").GetBoolean());
        Assert.Single((await other.GetFromJsonAsync<JsonElement>("/api/banks/connections")).EnumerateArray());
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<LedgerraDbContext>(); Assert.Empty(await db.BankFeedEntries.Where(x => x.ConnectionId == connection).ToListAsync());
    }

}
