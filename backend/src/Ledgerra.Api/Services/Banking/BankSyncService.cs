using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Infrastructure.Persistence;
using Ledgerra.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
namespace Ledgerra.Api.Services.Banking;

public sealed class BankSyncService(LedgerraDbContext db, IPlaidClient plaid, BankSecretProtector protector)
{
    public async Task<int> SyncAsync(Guid userId, Guid connectionId, CancellationToken ct)
    {
        var connection = await db.BankConnections.AsNoTracking().SingleAsync(x => x.UserId == userId && x.Id == connectionId, ct);
        var settings = await db.BankProviderSettings.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
        if (connection.Environment != settings.Environment) throw new BankProviderException("ENVIRONMENT_MISMATCH");
        var secret = protector.Unprotect(settings.EncryptedSecret); var accessToken = protector.Unprotect(connection.EncryptedAccessToken);
        var cursor = connection.Cursor; var changes = new List<(JsonElement Row, bool Modified, bool Removed)>();
        var complete = false;
        // Keep the original cursor until the entire paginated update has succeeded.
        for (var page = 0; page < 25; page++)
        {
            var payload = new Dictionary<string, object?> { ["access_token"] = accessToken, ["count"] = 500 };
            if (cursor.Length > 0) payload["cursor"] = cursor;
            var result = await plaid.SendAsync(settings, secret, "/transactions/sync", payload, ct);
            foreach (var row in result.GetProperty("added").EnumerateArray()) changes.Add((row, false, false));
            foreach (var row in result.GetProperty("modified").EnumerateArray()) changes.Add((row, true, false));
            foreach (var row in result.GetProperty("removed").EnumerateArray()) changes.Add((row, false, true));
            var cursorValue = result.GetProperty("next_cursor");
            if (cursorValue.ValueKind != JsonValueKind.String || cursorValue.GetString()!.Length > 4096) throw new BankProviderException("INVALID_CURSOR");
            cursor = cursorValue.GetString()!;
            if (cursor.Length == 0 && result.GetProperty("has_more").GetBoolean()) throw new BankProviderException("INVALID_CURSOR");
            if (!result.GetProperty("has_more").GetBoolean()) { complete = true; break; }
        }
        if (!complete) throw new BankProviderException("SYNC_PAGE_LIMIT");
        var accountsResponse = await plaid.SendAsync(settings, secret, "/accounts/get", new() { ["access_token"] = accessToken }, ct);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var current = await db.BankConnections.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == connectionId, ct);
        if (current is null || current.Cursor != connection.Cursor || current.EncryptedAccessToken != connection.EncryptedAccessToken) throw new BankProviderException("SYNC_CHANGED_RETRY");
        await UpsertAccounts(userId, connectionId, accountsResponse.GetProperty("accounts"), ct);
        var feed = await db.BankFeedEntries.Where(x => x.UserId == userId && x.ConnectionId == connectionId).ToDictionaryAsync(x => x.ProviderTransactionId, ct);
        foreach (var (row, modified, removed) in changes)
        {
            var providerId = Text(row, "transaction_id", 200);
            if (removed) { if (feed.TryGetValue(providerId, out var old)) old.Removed = true; continue; }
            var accountId = Text(row, "account_id", 200);
            var amount = row.GetProperty("amount").GetDecimal();
            if (Math.Abs(amount) > 999999999 || amount != decimal.Round(amount, 2)) throw new BankProviderException("UNSUPPORTED_AMOUNT_PRECISION");
            if (!DateOnly.TryParseExact(Text(row, "date", 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new BankProviderException("INVALID_TRANSACTION_DATE");
            if (!feed.TryGetValue(providerId, out var entry))
            {
                entry = new BankFeedEntry { Id = Guid.NewGuid(), UserId = userId, ConnectionId = connectionId, ProviderTransactionId = providerId };
                db.BankFeedEntries.Add(entry); feed[providerId] = entry;
            }
            entry.Modified |= modified || (entry.ProviderAccountId.Length > 0 && (entry.ProviderAccountId != accountId || entry.Amount != amount || entry.DateUtc.Date != date.ToDateTime(TimeOnly.MinValue) || entry.Name != ShortText(row, "name", 400)));
            entry.ProviderAccountId = accountId; entry.Amount = amount; entry.DateUtc = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            entry.Currency = ShortText(row, "iso_currency_code", 3).ToUpperInvariant();
            entry.Name = ShortText(row, "name", 400); entry.Payee = ShortText(row, "merchant_name", 120);
            entry.Pending = row.GetProperty("pending").GetBoolean(); entry.Removed = false;
            if (entry.ExternalKey.Length == 0) entry.ExternalKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"Plaid|{connection.Environment}|{connection.ItemId}|{providerId}")));
        }
        current.Cursor = cursor; current.LastSyncedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return changes.Count;
    }

    public async Task UpsertAccounts(Guid userId, Guid connectionId, JsonElement rows, CancellationToken ct)
    {
        var existing = await db.ConnectedBankAccounts.Where(x => x.UserId == userId && x.ConnectionId == connectionId).ToDictionaryAsync(x => x.ProviderAccountId, ct);
        foreach (var row in rows.EnumerateArray())
        {
            var id = Text(row, "account_id", 200);
            if (!existing.TryGetValue(id, out var account))
            { account = new ConnectedBankAccount { Id = Guid.NewGuid(), UserId = userId, ConnectionId = connectionId, ProviderAccountId = id }; db.ConnectedBankAccounts.Add(account); existing[id] = account; }
            account.Name = ShortText(row, "name", 120); account.Mask = new string(ShortText(row, "mask", 8).Where(char.IsDigit).TakeLast(4).ToArray());
            account.Type = ShortText(row, "type", 40);
            if (account.Type is not ("depository" or "credit")) account.LedgerAccountId = null;
            var balances = row.GetProperty("balances");
            var currency = ShortText(balances, "iso_currency_code", 3).ToUpperInvariant();
            if (account.Currency != currency) account.LedgerAccountId = null;
            account.Currency = currency;
            account.ReportedBalance = balances.TryGetProperty("current", out var balance) && balance.ValueKind == JsonValueKind.Number ? decimal.Round(balance.GetDecimal(), 2) : null;
        }
    }
    public static string Text(JsonElement row, string key, int max)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > max) throw new BankProviderException("INVALID_RESPONSE_FIELD");
        return value.GetString()!;
    }
    public static string ShortText(JsonElement row, string key, int max) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "")[..Math.Min(value.GetString()!.Length, max)] : "";
}
