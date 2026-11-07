using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Api.Services.Banking;
using Ledgerra.Api.Services.Imports;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Infrastructure.Persistence;
using Ledgerra.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController, Authorize, BankProviderErrors, Route("api/banks")]
public sealed class BankConnectionsController(LedgerraDbContext db, IPlaidClient plaid, BankSecretProtector protector,
    BankSyncService sync, IImportCategorizationRuleMatcher rules, IImportDuplicateDetector duplicates) : ControllerBase
{
    private Guid UserId => User.GetRequiredUserId();
    private Task<BankProviderSettings?> Settings(CancellationToken ct) => db.BankProviderSettings.SingleOrDefaultAsync(x => x.UserId == UserId, ct);

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var settings = await Settings(ct);
        return Ok(new { IsConfigured = settings is not null, ClientId = settings?.ClientId ?? "", Environment = settings?.Environment ?? "Sandbox", Country = settings?.Country ?? "US", settings?.RedirectUri });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(BankSettingsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || request.Environment is not ("Sandbox" or "Production") || request.Country is not ("US" or "CA")) return BadRequest();
        if (!string.IsNullOrWhiteSpace(request.RedirectUri) && (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "https" || redirect.UserInfo.Length > 0 || redirect.Fragment.Length > 0 || redirect.AbsolutePath.TrimEnd('/') != "/imports"))
            return BadRequest(new ProblemDetails { Title = "The OAuth redirect must be your HTTPS Ledgerra /imports URL, without a fragment or credentials." });
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var settings = await Settings(ct);
        if (settings is not null && (settings.ClientId != request.ClientId.Trim() || settings.Environment != request.Environment) && await db.BankConnections.AnyAsync(x => x.UserId == UserId, ct))
            return Conflict(new ProblemDetails { Title = "Disconnect existing banks before changing the Plaid client or environment." });
        if (settings is null)
        {
            if (string.IsNullOrWhiteSpace(request.Secret)) return BadRequest(new ProblemDetails { Title = "Enter the Plaid secret in this settings form." });
            settings = new BankProviderSettings { UserId = UserId }; db.BankProviderSettings.Add(settings);
        }
        if (!string.IsNullOrWhiteSpace(request.Secret)) settings.EncryptedSecret = protector.Protect(request.Secret.Trim());
        settings.ClientId = request.ClientId.Trim(); settings.Environment = request.Environment; settings.Country = request.Country;
        settings.RedirectUri = string.IsNullOrWhiteSpace(request.RedirectUri) ? null : request.RedirectUri.Trim();
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return NoContent();
    }

    [HttpDelete("settings")]
    public async Task<IActionResult> DeleteSettings(CancellationToken ct)
    {
        if (await db.BankConnections.AnyAsync(x => x.UserId == UserId, ct)) return Conflict(new ProblemDetails { Title = "Disconnect banks before removing provider credentials." });
        var settings = await Settings(ct); if (settings is not null) { db.BankProviderSettings.Remove(settings); await db.SaveChangesAsync(ct); } return NoContent();
    }

    [HttpPost("link-token")]
    public async Task<IActionResult> LinkToken(BankLinkRequest request, CancellationToken ct)
    {
        var settings = await Settings(ct); if (settings is null) return BadRequest(new ProblemDetails { Title = "Configure Plaid first." });
        var payload = new Dictionary<string, object?> { ["client_name"] = "Ledgerra", ["language"] = "en", ["country_codes"] = new[] { settings.Country }, ["user"] = new { client_user_id = UserId.ToString() } };
        if (settings.RedirectUri is not null) payload["redirect_uri"] = settings.RedirectUri;
        if (request.ConnectionId.HasValue)
        {
            var connection = await db.BankConnections.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == request.ConnectionId, ct);
            if (connection is null) return NotFound(); payload["access_token"] = protector.Unprotect(connection.EncryptedAccessToken);
        }
        else payload["products"] = new[] { "transactions" };
        var response = await plaid.SendAsync(settings, protector.Unprotect(settings.EncryptedSecret), "/link/token/create", payload, ct);
        return Ok(new { LinkToken = BankSyncService.Text(response, "link_token", 1000) });
    }

    [HttpPost("exchange")]
    public async Task<IActionResult> Exchange(BankExchangeRequest request, CancellationToken ct)
    {
        var settings = await Settings(ct); if (settings is null) return BadRequest(new ProblemDetails { Title = "Configure Plaid first." });
        if (await db.BankConnections.CountAsync(x => x.UserId == UserId, ct) >= 10) return BadRequest(new ProblemDetails { Title = "Keep at most ten bank connections." });
        var secret = protector.Unprotect(settings.EncryptedSecret);
        var response = await plaid.SendAsync(settings, secret, "/item/public_token/exchange", new() { ["public_token"] = request.PublicToken }, ct);
        var itemId = BankSyncService.Text(response, "item_id", 200); var accessToken = BankSyncService.Text(response, "access_token", 1000);
        if (await db.BankConnections.AnyAsync(x => x.UserId == UserId && x.ItemId == itemId, ct)) return Conflict(new ProblemDetails { Title = "This bank connection is already linked." });
        try
        {
            var accounts = await plaid.SendAsync(settings, secret, "/accounts/get", new() { ["access_token"] = accessToken }, ct);
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var connection = new BankConnection { Id = Guid.NewGuid(), UserId = UserId, ItemId = itemId, Environment = settings.Environment, EncryptedAccessToken = protector.Protect(accessToken), Name = string.IsNullOrWhiteSpace(request.Name) ? "Bank connection" : request.Name.Trim() };
            db.BankConnections.Add(connection); await sync.UpsertAccounts(UserId, connection.Id, accounts.GetProperty("accounts"), ct);
            await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
            return Ok(new { connection.Id, connection.Name });
        }
        catch
        {
            // Best-effort revoke if exchanging succeeded but local linking failed.
            try { await plaid.SendAsync(settings, secret, "/item/remove", new() { ["access_token"] = accessToken }, CancellationToken.None); } catch { }
            throw;
        }
    }

    [HttpGet("connections")]
    public async Task<IActionResult> Connections(CancellationToken ct)
    {
        var accounts = await db.ConnectedBankAccounts.Where(x => x.UserId == UserId).ToListAsync(ct);
        var connections = await db.BankConnections.Where(x => x.UserId == UserId).OrderBy(x => x.Name).ToListAsync(ct);
        return Ok(connections.Select(x => new { x.Id, x.Name, x.Environment, x.LastSyncedAtUtc, Accounts = accounts.Where(a => a.ConnectionId == x.Id).Select(a => new { a.Id, a.Name, a.Mask, a.Currency, a.Type, a.ReportedBalance, a.LedgerAccountId }) }));
    }

    [HttpPut("accounts/{id:guid}/mapping")]
    public async Task<IActionResult> Map(Guid id, BankAccountMappingRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var source = await db.ConnectedBankAccounts.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct); if (source is null) return NotFound();
        if (request.AccountId.HasValue)
        {
            var account = await db.Accounts.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == request.AccountId, ct); if (account is null) return NotFound();
            if (source.Type is not ("depository" or "credit") || source.Currency.Length != 3 || source.Currency != account.CurrencyCode) return BadRequest(new ProblemDetails { Title = "Choose a bank/card account in the same supported currency. Investments and loans are not supported." });
            if (await db.ConnectedBankAccounts.AnyAsync(x => x.UserId == UserId && x.Id != id && x.LedgerAccountId == request.AccountId, ct)) return Conflict(new ProblemDetails { Title = "This Ledgerra account is already mapped to another connected account." });
        }
        source.LedgerAccountId = request.AccountId; await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return NoContent();
    }

    [HttpPost("connections/{id:guid}/sync")]
    public async Task<IActionResult> Sync(Guid id, CancellationToken ct)
    {
        if (!await db.BankConnections.AnyAsync(x => x.UserId == UserId && x.Id == id, ct)) return NotFound();
        var changes = await sync.SyncAsync(UserId, id, ct);
        var current = await db.BankConnections.SingleAsync(x => x.UserId == UserId && x.Id == id, ct);
        return Ok(new { Changes = changes, InitialDataPending = current.Cursor.Length == 0 });
    }

    [HttpDelete("connections/{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        var connection = await db.BankConnections.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct); if (connection is null) return NotFound();
        var settings = await Settings(ct); if (settings is null) return Conflict();
        // Revoke remotely before local deletion. Failure leaves the connection available to retry.
        try { await plaid.SendAsync(settings, protector.Unprotect(settings.EncryptedSecret), "/item/remove", new() { ["access_token"] = protector.Unprotect(connection.EncryptedAccessToken) }, ct); }
        catch (BankProviderException e) when (e.Code is "ITEM_NOT_FOUND" or "INVALID_ACCESS_TOKEN") { /* Already revoked remotely. */ }
        db.BankFeedEntries.RemoveRange(db.BankFeedEntries.Where(x => x.UserId == UserId && x.ConnectionId == id));
        db.ConnectedBankAccounts.RemoveRange(db.ConnectedBankAccounts.Where(x => x.UserId == UserId && x.ConnectionId == id));
        db.BankConnections.Remove(connection); await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPost("connections/{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        if (!await db.BankConnections.AnyAsync(x => x.UserId == UserId && x.Id == id, ct)) return NotFound();
        var mappings = await db.ConnectedBankAccounts.Where(x => x.UserId == UserId && x.ConnectionId == id).ToDictionaryAsync(x => x.ProviderAccountId, ct);
        var owned = await db.Accounts.Where(x => x.UserId == UserId).ToDictionaryAsync(x => x.Id, ct);
        var feed = await db.BankFeedEntries.Where(x => x.UserId == UserId && x.ConnectionId == id).OrderBy(x => x.DateUtc).ToListAsync(ct);
        var keys = feed.Select(x => x.ExternalKey).ToArray();
        var imported = (await db.EntryMetadata.Where(x => x.UserId == UserId && x.ExternalKey != null && keys.Contains(x.ExternalKey)).Select(x => x.ExternalKey!).ToListAsync(ct)).ToHashSet();
        var warnings = new List<string>(); var initial = new List<ImportDraftReviewItem>(); var eligible = new Dictionary<string, BankFeedEntry>();
        foreach (var row in feed)
        {
            if (imported.Contains(row.ExternalKey))
            {
                if (row.Removed || row.Modified) warnings.Add($"{row.DateUtc:yyyy-MM-dd} {row.Name}: the bank {(row.Removed ? "removed" : "changed")} an already imported transaction. Review it in Ledgerra; it has not been overwritten or deleted.");
                continue;
            }
            if (row.Removed || row.Pending) continue;
            if (row.Amount == 0) { warnings.Add($"Skipped zero amount: {row.Name}."); continue; }
            if (!mappings.TryGetValue(row.ProviderAccountId, out var map) || !map.LedgerAccountId.HasValue || !owned.TryGetValue(map.LedgerAccountId.Value, out var account)) continue;
            if (row.Currency.Length != 3 || row.Currency != account.CurrencyCode || row.Currency != map.Currency) { warnings.Add($"Skipped a currency mismatch: {row.Name}."); continue; }
            var type = row.Amount > 0 ? "Expense" : "Income";
            if (row.Amount < 0 && System.Text.RegularExpressions.Regex.IsMatch(row.Name, @"\b(refund|refunded|reversal|returned)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) type = "Refund";
            var rowWarnings = row.Amount < 0 ? new[] { "Money received: review whether this is income, a refund or an internal transfer." } : Array.Empty<string>();
            initial.Add(ImportDraftReviewItem.FromAnalyzedDraft(row.Id.ToString(), account.Id, null, Math.Abs(row.Amount), type, row.DateUtc, row.Name, 1, rowWarnings));
            eligible[row.Id.ToString()] = row;
        }
        if (initial.Count > 2000) return BadRequest(new ProblemDetails { Title = "More than 2,000 unimported transactions. Map and review one source account at a time." });
        var reviewed = await duplicates.MarkDuplicatesAsync(UserId, await rules.ApplyAsync(UserId, initial, ct), ct);
        var drafts = reviewed.Select(d => { var row = eligible[d.SourceId]; return new MonthlyReportDraftTransactionResponse(d.SourceId, d.AccountId, d.CategoryId, d.Amount, d.Type, d.OccurredOnUtc, d.Note, d.Confidence, d.Warnings, d.AppliedRuleId, d.AppliedRuleName, d.IsLikelyDuplicate, d.DuplicateTransactionId, d.DuplicateReason, false, row.Payee.Length > 0 ? row.Payee : row.Name[..Math.Min(row.Name.Length, 120)], null, null, row.ExternalKey); }).ToList();
        var unmapped = feed.Count(x => !x.Pending && !x.Removed && (!mappings.TryGetValue(x.ProviderAccountId, out var map) || !map.LedgerAccountId.HasValue));
        if (unmapped > 0) warnings.Add($"{unmapped} posted transactions are in skipped or unmapped source accounts.");
        warnings.Insert(0, $"{feed.Count(x => x.Pending && !x.Removed)} pending transactions excluded. {drafts.Count} posted transactions ready for review. Nothing is selected automatically.");
        return Ok(new MonthlyReportAnalysisResponse(drafts, warnings));
    }
}
public sealed class BankSettingsRequest
{
    [Required, MaxLength(200)] public string ClientId { get; init; } = "";
    [MaxLength(1000)] public string? Secret { get; init; }
    [Required] public string Environment { get; init; } = "Sandbox";
    [Required] public string Country { get; init; } = "US";
    [MaxLength(2048)] public string? RedirectUri { get; init; }
}
public sealed class BankLinkRequest { public Guid? ConnectionId { get; init; } }
public sealed class BankExchangeRequest
{
    [Required, MaxLength(1000)] public string PublicToken { get; init; } = "";
    [MaxLength(120)] public string Name { get; init; } = "Bank connection";
}
public sealed class BankAccountMappingRequest { public Guid? AccountId { get; init; } }
