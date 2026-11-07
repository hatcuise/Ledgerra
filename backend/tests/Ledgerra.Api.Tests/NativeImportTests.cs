using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ledgerra.Api.Services.Imports;
using Microsoft.AspNetCore.Http;

namespace Ledgerra.Api.Tests;

public sealed class NativeImportTests : IClassFixture<LedgerraApiFactory>
{
    private readonly LedgerraApiFactory _factory;
    public NativeImportTests(LedgerraApiFactory factory) => _factory = factory;
    private static IFormFile File(string text, string name = "bank.ofx")
    {
        var bytes = Encoding.UTF8.GetBytes(text); return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }
    private static string Ofx(string memo = "Transfer from savings", string amount = "25", string id = "BANK-TXN-123") => $"""
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102

        <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD
        <BANKACCTFROM><BANKID>123456<ACCTID>123456789<ACCTTYPE>CHECKING</BANKACCTFROM>
        <BANKTRANLIST><STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260410120000[-5:EST]<TRNAMT>{amount}<FITID>{id}<NAME>Bank transfer<MEMO>{memo}</STMTTRN></BANKTRANLIST>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
        """;

    [Fact]
    public async Task OfxLegacySgml_PreservesBankIdentityAndMasksAccountNumber()
    {
        var reader = new NativeStatementReader(); var document = await reader.ReadAsync(File(Ofx())); var entry = Assert.Single(document.Entries);
        Assert.Equal(25, entry.SignedAmount); Assert.Equal(new DateTime(2026,4,10,0,0,0,DateTimeKind.Utc), entry.Date); Assert.Equal(64, entry.ExternalKey!.Length);
        Assert.DoesNotContain("123456789", document.Accounts[0].Name); Assert.Equal("USD", document.Accounts[0].Currency);
        var updated = await reader.ReadAsync(File(Ofx("Changed description"))); Assert.Equal(entry.ExternalKey, updated.Entries[0].ExternalKey);
        var differentId = await reader.ReadAsync(File(Ofx(id: "OTHER-TXN"))); Assert.NotEqual(entry.ExternalKey, differentId.Entries[0].ExternalKey);
    }

    [Fact]
    public async Task OfxXmlCreditCard_IsReadWithoutAi()
    {
        var xml = "<?xml version=\"1.0\"?><OFX><CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><CURDEF>USD</CURDEF><CCACCTFROM><ACCTID>1234</ACCTID></CCACCTFROM><BANKTRANLIST><STMTTRN><DTPOSTED>20260411</DTPOSTED><TRNAMT>-50.25</TRNAMT><FITID>CC-1</FITID><NAME>Market &amp; shop</NAME></STMTTRN></BANKTRANLIST></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>";
        var document = await new NativeStatementReader().ReadAsync(File(xml, "card.qfx")); Assert.Equal(-50.25m, document.Entries[0].SignedAmount); Assert.Equal("Market & shop", document.Entries[0].Payee);
    }

    [Fact]
    public async Task Qif_HandlesMultipleAccountsDatesAndExplicitTransferHints()
    {
        var qif = "!Account\nNChecking\nTBank\n^\n!Type:Bank\nD4/10'26\nT-25.00\nPTransfer\nL[Savings]\n^\n!Account\nNSavings\nTBank\n^\n!Type:Bank\nD4/11/2026\nT25.00\nPTransfer\nL[Checking]\n^\n";
        var document = await new NativeStatementReader().ReadAsync(File(qif, "bank.qif")); Assert.Equal(2, document.Accounts.Count); Assert.Equal(2, document.Entries.Count); Assert.StartsWith("Transfer to", document.Entries[0].Memo);
        var european = await new NativeStatementReader().ReadAsync(File(qif, "bank.qif"), dateOrder: "dmy"); Assert.Equal(10, european.Entries[0].Date.Month);
    }

    [Fact]
    public async Task Qif_ExcludesOpeningAndSplitEntriesWithWarnings()
    {
        var qif = "!Type:Bank\nD4/1/2026\nT100\nPOpening Balance\n^\nD4/2/2026\nT-50\nSFood\n$-25\nSTravel\n$-25\n^\nD4/3/2026\nT-20\nPStore\n^\n";
        var d = await new NativeStatementReader().ReadAsync(File(qif, "export.qif")); Assert.Single(d.Entries); Assert.Equal(100, d.Accounts[0].OpeningBalance); Assert.Equal(2, d.Warnings.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeStatementReader().ReadAsync(File("!Type:Bank\nD4/1/2026\nT1,23\n^", "bad.qif")));
    }

    [Fact]
    public async Task HomeBank_ResolvesAccountPayeeCategoryTagsAndJulianDate()
    {
        var julian = new DateOnly(2026,4,10).DayNumber + 1;
        var xml = $"<homebank v=\"1.3\"><properties curr=\"1\"/><cur key=\"1\" iso=\"USD\"/><account key=\"2\" name=\"Bank\" initial=\"100\"/><pay key=\"3\" name=\"Store\"/><cat key=\"4\" name=\"Household\"/><cat key=\"5\" parent=\"4\" name=\"Food\"/><ope date=\"{julian}\" amount=\"-25\" account=\"2\" payee=\"3\" category=\"5\" wording=\"Groceries\" info=\"123\" tags=\"household\"/></homebank>";
        var document = await new NativeStatementReader().ReadAsync(File(xml, "wallet.xhb")); var row = Assert.Single(document.Entries);
        Assert.Equal("Household:Food", row.Category); Assert.Equal("Store", row.Payee); Assert.Equal("household", row.Tags); Assert.Equal(10, row.Date.Day); Assert.Equal("USD", document.Accounts[0].Currency);
    }

    [Fact]
    public async Task ReadersRejectExternalEntitiesAndOfxCorrections()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeStatementReader().ReadAsync(File("<!DOCTYPE homebank [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><homebank>&x;</homebank>", "bad.xhb")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeStatementReader().ReadAsync(File(Ofx().Replace("<FITID>", "<CORRECTFITID>old</CORRECTFITID><FITID>"))));
    }

    private async Task<HttpClient> Client()
    {
        var c = _factory.CreateClient(); var r = await c.PostAsJsonAsync("/api/auth/register", new { login = "native-" + Guid.NewGuid().ToString("N")[..10], password = "P@ssw0rd123!" }); r.EnsureSuccessStatusCode();
        var a = await r.Content.ReadFromJsonAsync<JsonElement>(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.GetProperty("accessToken").GetString()); return c;
    }
    private static async Task<Guid> Account(HttpClient c, string currency = "USD")
    {
        var r = await c.PostAsJsonAsync("/api/accounts", new { name = "Native bank", type = "Checking", currencyCode = currency }); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static MultipartFormDataContent Upload(string text, Dictionary<string, Guid?>? mappings = null)
    {
        var f = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text)); file.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx"); f.Add(file, "file", "bank.ofx");
        if (mappings is not null) f.Add(new StringContent(JsonSerializer.Serialize(mappings)), "accountMappings"); return f;
    }

    [Fact]
    public async Task NativePreviewCommit_FitidCannotBeImportedAgainEvenAfterEditing()
    {
        using var c = await Client(); var account = await Account(c);
        using var inspect = Upload(Ofx()); var inspected = await c.PostAsync("/api/imports/native/inspect", inspect); inspected.EnsureSuccessStatusCode();
        var key = (await inspected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accounts")[0].GetProperty("key").GetString()!;
        async Task<JsonElement> Preview(string memo)
        {
            using var form = Upload(Ofx(memo), new() { [key] = account }); var r = await c.PostAsync("/api/imports/native/preview", form); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactions");
        }
        var drafts = await Preview("Bank deposit"); var save = await c.PostAsJsonAsync("/api/imports/monthly-report/commit", new { transactions = drafts }); save.EnsureSuccessStatusCode();
        var id = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created")[0].GetProperty("id").GetGuid();
        var update = await c.PutAsJsonAsync($"/api/transactions/{id}", new { amount = 25, type = "Income", occurredOnUtc = "2026-04-10T00:00:00Z", note = "Edited description" }); update.EnsureSuccessStatusCode();
        var backup = await c.GetFromJsonAsync<JsonElement>("/api/backup/export");
        (await c.PostAsJsonAsync("/api/backup/restore", backup)).EnsureSuccessStatusCode();
        var repeated = await Preview("Different bank memo"); Assert.True(repeated[0].GetProperty("isLikelyDuplicate").GetBoolean());
        var duplicate = await c.PostAsJsonAsync("/api/imports/monthly-report/commit", new { transactions = repeated, acceptedDuplicateSourceIds = new[] { repeated[0].GetProperty("sourceId").GetString() } }); Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Single((await c.GetFromJsonAsync<JsonElement>("/api/transactions")).EnumerateArray());
    }

    [Fact]
    public async Task NativeMappingRejectsForeignAccountsAndWrongCurrency()
    {
        using var owner = await Client(); using var stranger = await Client(); var foreign = await Account(stranger); var euro = await Account(owner, "EUR");
        var d = await new NativeStatementReader().ReadAsync(File(Ofx())); var key = d.Accounts[0].Key;
        using var form = Upload(Ofx(), new() { [key] = foreign }); Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync("/api/imports/native/preview", form)).StatusCode);
        using var wrongCurrency = Upload(Ofx(), new() { [key] = euro }); Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/imports/native/preview", wrongCurrency)).StatusCode);
    }
}
