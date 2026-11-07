using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ledgerra.Api.Services.Imports;

public sealed record NativeAccount(string Key, string Name, string? Currency, decimal? OpeningBalance = null);
public sealed record NativeEntry(string SourceId, string AccountKey, DateTime Date, decimal SignedAmount,
    string? Payee, string? Memo, string? Category, string? Reference, string? Tags, string? ExternalKey,
    IReadOnlyList<string> Warnings, string? CounterpartyAccountKey = null);
public sealed record NativeStatement(string Format, IReadOnlyList<NativeAccount> Accounts, IReadOnlyList<NativeEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>Independent readers for documented bank interchange fields. Never executes wallet code.</summary>
public sealed class NativeStatementReader
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    public static string Identity(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<NativeStatement> ReadAsync(IFormFile file, string encoding = "utf-8", string dateOrder = "mdy", CancellationToken ct = default)
    {
        if (file.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidOperationException("Statement must contain data and be 8 MB or smaller.");
        if (encoding is not ("utf-8" or "windows-1252") || dateOrder is not ("mdy" or "dmy")) throw new InvalidOperationException("Choose a supported encoding and date order.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".ofx" or ".qfx" or ".qif" or ".xhb")) throw new InvalidOperationException("Choose an OFX, QFX, QIF, or HomeBank .xhb file.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var selectedEncoding = Encoding.GetEncoding(encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, selectedEncoding, detectEncodingFromByteOrderMarks: true);
        string content;
        try { content = await reader.ReadToEndAsync(ct); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("The file encoding did not match. Try Windows-1252 or export UTF-8."); }
        if (content.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Decoded statement exceeds the size limit.");
        NativeStatement result;
        try { result = extension switch { ".ofx" or ".qfx" => Ofx(content), ".qif" => Qif(content, dateOrder), _ => HomeBank(content) }; }
        catch (RegexMatchTimeoutException) { throw new InvalidOperationException("Statement markup is too complex to read."); }
        catch (ArgumentException) { throw new InvalidOperationException("The statement contains invalid or duplicate source identifiers."); }
        catch (XmlException) { throw new InvalidOperationException("Invalid HomeBank XML. DTDs and external entities are not supported."); }
        if (result.Entries.Count > 20000) throw new InvalidOperationException("Split the statement into batches of at most 20,000 transactions.");
        if (result.Entries.Count == 0) throw new InvalidOperationException("No supported nonzero transactions were found. Check the format and unsupported-record warnings in the original export.");
        if (result.Accounts.Count > 100) throw new InvalidOperationException("Split the statement into at most 100 source accounts.");
        return result;
    }

    public static void ValidateEntry(NativeEntry row)
    {
            if (row.SignedAmount == 0 || row.SignedAmount is > 999999999 or < -999999999 || decimal.Round(row.SignedAmount, 2) != row.SignedAmount)
                throw new InvalidOperationException($"Transaction {row.SourceId} has an unsupported amount or precision. Ledgerra currently stores two decimal places.");
            if (row.Payee?.Length > 120 || row.Memo?.Length > 400 || row.Reference?.Length > 120 || row.Tags?.Length > 400 || row.Category?.Length > 120)
                throw new InvalidOperationException($"Transaction {row.SourceId} exceeds a supported text-field length. No data was imported.");
    }

    private static decimal Amount(string text)
    {
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var amount))
            throw new InvalidOperationException($"Invalid statement amount '{text}'.");
        return amount;
    }
    private static string? Value(string body, string tag)
    {
        var match = Pattern($@"<{Regex.Escape(tag)}\b[^>]*>\s*([^<]*)").Match(body);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : null;
    }
    private static DateTime IsoBankDate(string value)
    {
        if (value.Length < 8 || !DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException($"Invalid OFX posted date '{value}'.");
        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }

    private static NativeStatement Ofx(string text)
    {
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("OFX entities and document types are not supported.");
        var accounts = new Dictionary<string, NativeAccount>(); var entries = new List<NativeEntry>(); var warnings = new List<string>();
        var statements = Pattern(@"<(?<kind>STMTRS|CCSTMTRS)\b[^>]*>(?<body>[\s\S]*?)</\k<kind>\s*>").Matches(text);
        if (statements.Count == 0) throw new InvalidOperationException("No OFX bank/credit-card statement was found. Investment statements are not supported.");
        foreach (Match statement in statements)
        {
            var body = statement.Groups["body"].Value;
            var accountNumber = Value(body, "ACCTID") ?? throw new InvalidOperationException("OFX account ID is missing.");
            if (string.IsNullOrWhiteSpace(accountNumber)) throw new InvalidOperationException("OFX account ID is empty.");
            var key = "ofx-" + Identity($"{Value(body, "BANKID")}|{Value(body, "BRANCHID")}|{accountNumber}|{statement.Groups["kind"].Value.ToUpperInvariant()}");
            var currency = Value(body, "CURDEF");
            if (currency is not null && !Pattern("^[A-Z]{3}$").IsMatch(currency)) throw new InvalidOperationException("Invalid OFX currency code.");
            accounts.TryAdd(key, new NativeAccount(key, $"{(statement.Groups["kind"].Value.Equals("CCSTMTRS", StringComparison.OrdinalIgnoreCase) ? "Credit card" : "Bank account")} …{accountNumber[^Math.Min(4, accountNumber.Length)..]}", currency?.ToUpperInvariant()));
            foreach (Match row in Pattern(@"<STMTTRN\b[^>]*>([\s\S]*?)</STMTTRN\s*>").Matches(body))
            {
                var data = row.Groups[1].Value;
                if (Value(data, "CORRECTFITID") is not null || Value(data, "CORRECTACTION") is not null) throw new InvalidOperationException("OFX corrections require manual review in the original bank export; automatic replacement/deletion is not supported.");
                var amount = Amount(Value(data, "TRNAMT") ?? throw new InvalidOperationException("OFX transaction amount is missing."));
                if (amount == 0) { warnings.Add("A zero-amount OFX entry was skipped."); continue; }
                var payee = Value(data, "NAME"); var memo = Value(data, "MEMO"); var fitid = Value(data, "FITID");
                entries.Add(new NativeEntry($"ofx-{entries.Count + 1}", key, IsoBankDate(Value(data, "DTPOSTED") ?? ""), amount,
                    payee, memo ?? payee, null, Value(data, "CHECKNUM") ?? Value(data, "REFNUM") ?? fitid, null,
                    string.IsNullOrWhiteSpace(fitid) ? null : Identity($"OFX|{key}|{fitid}"), []));
            }
        }
        warnings.Add("OFX amounts keep their bank-defined sign. Review credits/refunds and card payments before saving. Statement balances are not applied as opening balances.");
        return new("OFX/QFX", accounts.Values.ToList(), entries, warnings.Distinct().ToList());
    }

    private static DateTime QifDate(string value, string order)
    {
        value = value.Replace(" ", "").Replace("'", "/");
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return DateTime.SpecifyKind(iso, DateTimeKind.Utc);
        var match = Pattern(@"^(\d{1,2})[/-](\d{1,2})[/-](\d{2}|\d{4})$").Match(value);
        if (!match.Success) throw new InvalidOperationException($"Invalid QIF date '{value}'. Choose the correct date order.");
        var first = int.Parse(match.Groups[1].Value); var second = int.Parse(match.Groups[2].Value); var year = int.Parse(match.Groups[3].Value);
        if (match.Groups[3].Length == 2) year += year >= 70 ? 1900 : 2000;
        try { return new DateTime(year, order == "mdy" ? first : second, order == "mdy" ? second : first, 0, 0, 0, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { throw new InvalidOperationException($"Invalid QIF date '{value}'. Choose the correct date order."); }
    }

    private static NativeStatement Qif(string text, string dateOrder)
    {
        var accounts = new Dictionary<string, NativeAccount>(); var entries = new List<NativeEntry>(); var warnings = new List<string>();
        var section = ""; var name = "QIF account"; var record = new List<string>();
        void ReadRecord()
        {
            if (record.Count == 0) return;
            string? Field(char field) => record.FirstOrDefault(s => s[0] == field)?[1..].Trim();
            if (section.Equals("Account", StringComparison.OrdinalIgnoreCase)) { name = Field('N') ?? name; return; }
            if (section.StartsWith("Option:", StringComparison.OrdinalIgnoreCase)) return;
            if (section.Equals("Type:Invst", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("QIF investment activity is not supported.");
            if (section.ToLowerInvariant() is not ("type:bank" or "type:cash" or "type:ccard" or "type:oth a" or "type:oth l" or "type:otha" or "type:othl")) return;
            var key = "qif-" + Identity(name); accounts.TryAdd(key, new NativeAccount(key, name, null));
            var amountText = Field('T') ?? Field('U'); if (amountText is null) throw new InvalidOperationException("QIF transaction amount is missing.");
            // QIF uses comma thousands separators with dot decimal regardless of UI locale.
            if (!Pattern(@"^[+-]?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?$").IsMatch(amountText)) throw new InvalidOperationException("QIF amounts require dot decimals and correctly grouped comma thousands.");
            var amount = Amount(amountText.Replace(",", "")); if (amount == 0) return;
            if (Field('P')?.Equals("Opening Balance", StringComparison.OrdinalIgnoreCase) == true)
            {
                accounts[key] = accounts[key] with { OpeningBalance = amount }; warnings.Add("QIF opening-balance entries were excluded. Review the destination account opening balance separately."); return;
            }
            if (record.Any(s => s[0] is 'S' or '$' or 'E')) { warnings.Add("QIF split transactions were excluded because this import does not yet preserve split allocations."); return; }
            string? counterparty = null;
            var category = Field('L'); var memo = Field('M') ?? Field('P');
            if (category?.StartsWith('[') == true)
            {
                counterparty = "qif-" + Identity(category.Trim('[', ']'));
                memo = $"Transfer {(amount < 0 ? "to" : "from")} {category.Trim('[', ']')}: {memo}"; category = null;
            }
            var rowWarnings = new List<string>();
            if (Field('C') is not null) rowWarnings.Add("Source clearing/reconciliation status is not applied. Reconcile this entry in Ledgerra.");
            entries.Add(new NativeEntry($"qif-{entries.Count + 1}", key, QifDate(Field('D') ?? "", dateOrder), amount,
                Field('P'), memo, category, Field('N'), null, null, rowWarnings, counterparty));
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart('\uFEFF'); if (line.Length == 0) continue;
            if (line.StartsWith('!')) { ReadRecord(); record.Clear(); section = line[1..].Trim(); continue; }
            if (line == "^") { ReadRecord(); record.Clear(); } else record.Add(line);
        }
        ReadRecord();
        return new("QIF", accounts.Values.ToList(), entries, warnings.Distinct().ToList());
    }

    private static NativeStatement HomeBank(string text)
    {
        using var xml = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        var document = XDocument.Load(xml);
        var root = document.Root;
        if (root?.Name.LocalName != "homebank") throw new InvalidOperationException("The file is not a HomeBank wallet.");
        string? A(XElement e, string name) => (string?)e.Attribute(name) is { } value && value != "(null)" && value.Length > 0 ? value : null;
        var currencies = root.Elements("cur").ToDictionary(e => A(e, "key") ?? "", e => A(e, "iso"));
        var accounts = root.Elements("account").Select(e => new NativeAccount("xhb-" + (A(e, "key") ?? throw new InvalidOperationException("HomeBank account key is missing.")),
            A(e, "name") ?? "HomeBank account", currencies.GetValueOrDefault(A(e, "curr") ?? (string?)root.Element("properties")?.Attribute("curr") ?? ""), A(e, "initial") is { } initial ? Amount(initial) : null)).ToList();
        var payees = root.Elements("pay").ToDictionary(e => A(e, "key") ?? "", e => A(e, "name"));
        var categories = root.Elements("cat").ToDictionary(e => A(e, "key") ?? "");
        string? CategoryName(string? key)
        {
            var names = new List<string>(); var seen = new HashSet<string>();
            while (key is not null && categories.TryGetValue(key, out var category))
            {
                if (!seen.Add(key)) throw new InvalidOperationException("HomeBank category hierarchy contains a cycle.");
                names.Insert(0, A(category, "name") ?? ""); key = A(category, "parent");
            }
            return names.Count == 0 ? null : string.Join(":", names);
        }
        var entries = new List<NativeEntry>(); var warnings = new List<string>();
        foreach (var row in root.Elements("ope"))
        {
            var flags = int.TryParse(A(row, "flags"), out var f) ? f : 0;
            var status = int.TryParse(A(row, "st"), out var st) ? st : 0;
            if ((flags & (1 << 5)) != 0 || status is 3 or 4) { warnings.Add("HomeBank reminder/void entries were excluded; they must not become real money movements."); continue; }
            if (A(row, "scat") is not null || (flags & (1 << 8)) != 0) { warnings.Add("HomeBank split entries were excluded because split allocation migration is not yet supported."); continue; }
            var amount = Amount(A(row, "amount") ?? ""); if (amount == 0) continue;
            if ((flags & (1 << 4)) != 0) { warnings.Add("Cross-currency HomeBank transfers were excluded; source/destination amounts require explicit FX support."); continue; }
            if (!int.TryParse(A(row, "date"), out var julian) || julian is < 1 or > 3652059) throw new InvalidOperationException("Invalid HomeBank transaction date.");
            var key = "xhb-" + A(row, "account"); if (!accounts.Any(a => a.Key == key)) throw new InvalidOperationException("HomeBank entry references an unknown account.");
            var memo = A(row, "wording");
            if ((flags & (1 << 3)) != 0 || A(row, "dst_account") is { } destination && destination != "0")
                memo = $"Transfer: {memo}";
            var rowWarnings = new List<string>(); if (status != 0) rowWarnings.Add("Source clearing/reconciliation status is not applied. Reconcile this entry in Ledgerra.");
            entries.Add(new NativeEntry($"xhb-{entries.Count + 1}", key, DateTime.SpecifyKind(DateTime.MinValue.AddDays(julian - 1), DateTimeKind.Utc), amount,
                payees.GetValueOrDefault(A(row, "payee") ?? ""), memo, CategoryName(A(row, "category")), A(row, "info"), A(row, "tags"), null, rowWarnings, A(row, "dst_account") is { } target && target != "0" ? "xhb-" + target : null));
        }
        warnings.Add("HomeBank account opening balances, schedules, budgets, rules, and reconciliation histories are not changed by transaction import. Review each mapped account separately.");
        return new("HomeBank", accounts, entries, warnings.Distinct().ToList());
    }
}
