using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json;
using Ledgerra.Application.Reporting;
using Ledgerra.Api.Extensions;
using Ledgerra.Api.Services.Imports;
using Ledgerra.Domain.Accounts;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Domain.Transactions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController, Authorize, Route("api/bookkeeping")]
public sealed class BookkeepingController(LedgerraDbContext db, IMonthlyAccountBalanceSnapshotService snapshots, TransferRecognitionService transferRecognition) : ControllerBase
{
    private Guid UserId => User.GetRequiredUserId();

    [HttpGet("profiles")]
    public async Task<IActionResult> Profiles(CancellationToken ct) => Ok(await db.ImportProfiles.Where(x => x.UserId == UserId).OrderBy(x => x.Name).ToListAsync(ct));

    [HttpPost("profiles")]
    public async Task<IActionResult> SaveProfile(ProfileRequest request, CancellationToken ct)
    {
        try { request.Settings.Options.Validate(); }
        catch (InvalidOperationException e) { return BadRequest(new ProblemDetails { Title = e.Message }); }
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Settings.DateColumn)) return BadRequest();
        var json = JsonSerializer.Serialize(request.Settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (json.Length > 8000) return BadRequest();
        var name = request.Name.Trim();
        var profile = await db.ImportProfiles.SingleOrDefaultAsync(x => x.UserId == UserId && x.Name == name, ct);
        if (profile is null) { profile = new ImportProfile { Id = Guid.NewGuid(), UserId = UserId, Name = name }; db.ImportProfiles.Add(profile); }
        profile.SettingsJson = json;
        await db.SaveChangesAsync(ct);
        return Ok(profile);
    }

    [HttpDelete("profiles/{id:guid}")]
    public async Task<IActionResult> DeleteProfile(Guid id, CancellationToken ct)
    {
        var item = await db.ImportProfiles.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct);
        if (item is null) return NotFound();
        db.Remove(item); await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpGet("payees")]
    public async Task<IActionResult> Payees(CancellationToken ct) => Ok(await db.Payees.Where(x => x.UserId == UserId).OrderBy(x => x.Name).ToListAsync(ct));

    [HttpPost("payees")]
    public async Task<IActionResult> SavePayee(PayeeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest();
        if (request.DefaultCategoryId is Guid category && !await db.Categories.AnyAsync(x => x.UserId == UserId && x.Id == category, ct)) return NotFound();
        var name = request.Name.Trim();
        var item = await db.Payees.SingleOrDefaultAsync(x => x.UserId == UserId && x.Name == name, ct);
        if (item is null) { item = new Payee { Id = Guid.NewGuid(), UserId = UserId, Name = name }; db.Payees.Add(item); }
        item.DefaultCategoryId = request.DefaultCategoryId;
        await db.SaveChangesAsync(ct); return Ok(item);
    }

    [HttpGet("accounts/{accountId:guid}/register")]
    public async Task<IActionResult> Register(Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == accountId, ct);
        if (account is null) return NotFound();
        var entries = await db.Transactions.Where(x => x.UserId == UserId && x.AccountId == accountId && x.ParentTransactionId == null).OrderBy(x => x.OccurredOnUtc).ThenBy(x => x.Id).ToListAsync(ct);
        var metadata = await db.EntryMetadata.Where(x => x.UserId == UserId).ToDictionaryAsync(x => x.Id, ct);
        var running = account.OpeningBalance;
        var rows = entries.Select(x => { running += TransactionSemantics.BalanceAmount(x); return new {
            x.Id, x.CategoryId, x.Amount, Type = x.Type.ToString(), x.Note, x.OccurredOnUtc, x.TransferGroupId,
            Balance = running, Metadata = metadata.GetValueOrDefault(x.Id) ?? new EntryMetadata { Id = x.Id, UserId = UserId }
        }; }).ToList();
        return Ok(new { account.Id, account.Name, account.CurrencyCode, account.OpeningBalance, Balance = running, Entries = rows,
            Reconciliations = await db.Reconciliations.Where(x => x.UserId == UserId && x.AccountId == accountId).OrderByDescending(x => x.StatementDate).ToListAsync(ct) });
    }

    [HttpPut("entries/{id:guid}")]
    public async Task<IActionResult> UpdateEntry(Guid id, EntryRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var entry = await db.Transactions.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id && x.ParentTransactionId == null, ct);
        if (entry is null) return NotFound();
        var metadata = await db.EntryMetadata.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct);
        if (metadata?.Status == "Reconciled") return Conflict(new ProblemDetails { Title = "Reopen the statement before editing a reconciled entry." });
        if (request.Status is not ("Uncleared" or "Cleared")) return BadRequest();
        Payee? payee = null;
        if (request.PayeeId is Guid payeeId)
        {
            payee = await db.Payees.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == payeeId, ct);
            if (payee is null) return NotFound();
        }
        var categoryId = request.CategoryId ?? entry.CategoryId;
        if (categoryId is Guid category && !await db.Categories.AnyAsync(x => x.UserId == UserId && x.Id == category, ct)) return NotFound();
        if (entry.TransferGroupId is null && entry.SplitGroupId is null) entry.CategoryId = categoryId ?? payee?.DefaultCategoryId;
        metadata ??= new EntryMetadata { Id = id, UserId = UserId };
        if (db.Entry(metadata).State == EntityState.Detached) db.EntryMetadata.Add(metadata);
        metadata.PayeeId = request.PayeeId; metadata.Status = request.Status; metadata.Reference = request.Reference; metadata.Tags = request.Tags;
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Ok(metadata);
    }

    [HttpPut("entries/bulk")]
    public async Task<IActionResult> BulkEntries(BulkEntryRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        if (request.Ids.Count == 0 || request.Ids.Count != request.Ids.Distinct().Count() || request.Ids.Count > 1000) return BadRequest();
        var entries = await db.Transactions.Where(x => x.UserId == UserId && request.Ids.Contains(x.Id) && x.ParentTransactionId == null).ToListAsync(ct);
        if (entries.Count != request.Ids.Count) return NotFound();
        var metadata = await db.EntryMetadata.Where(x => x.UserId == UserId && request.Ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (metadata.Values.Any(x => x.Status == "Reconciled")) return Conflict();
        if (request.CategoryId is Guid category && !await db.Categories.AnyAsync(x => x.UserId == UserId && x.Id == category, ct)) return NotFound();
        if (request.PayeeId is Guid payee && !await db.Payees.AnyAsync(x => x.UserId == UserId && x.Id == payee, ct)) return NotFound();
        TransactionType? newType = null;
        if (request.Type is not null)
        {
            if (!Enum.TryParse<TransactionType>(request.Type, true, out var parsed) || parsed is not (TransactionType.Income or TransactionType.Expense or TransactionType.Refund)
                || entries.Any(x => x.TransferGroupId != null || x.SplitGroupId != null)) return BadRequest(new ProblemDetails { Title = "Reclassification supports ordinary entries only; edit splits or transfers separately." });
            newType = parsed;
        }
        foreach (var entry in entries)
        {
            if (newType.HasValue) entry.Type = newType.Value;
            if (request.CategoryId.HasValue && entry.TransferGroupId == null && entry.SplitGroupId == null) entry.CategoryId = request.CategoryId;
            if (request.PayeeId.HasValue)
            {
                if (!metadata.TryGetValue(entry.Id, out var m)) { m = new EntryMetadata { Id = entry.Id, UserId = UserId }; db.EntryMetadata.Add(m); }
                m.PayeeId = request.PayeeId;
            }
        }
        await db.SaveChangesAsync(ct);
        if (newType.HasValue)
        {
            foreach (var group in entries.GroupBy(x => x.AccountId))
            {
                var first = group.Min(x => x.OccurredOnUtc);
                await snapshots.RefreshFromAsync(UserId, new DateOnly(first.Year, first.Month, 1), group.Key, ct);
            }
        }
        if (tx is not null) await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("transfers/candidates")]
    public async Task<IActionResult> TransferCandidates([FromQuery, Range(0, 7)] int dayWindow = 3, CancellationToken ct = default)
    {
        try { return Ok(await transferRecognition.FindAsync(UserId, dayWindow, ct)); }
        catch (InvalidOperationException e) { return BadRequest(new ProblemDetails { Title = e.Message }); }
    }

    [HttpPost("transfers/recognize")]
    public async Task<IActionResult> RecognizeTransfers([FromQuery, Range(0, 7)] int dayWindow = 3, CancellationToken ct = default)
    {
        try { return Ok(new { Linked = await transferRecognition.RecognizeAsync(UserId, null, dayWindow, ct) }); }
        catch (InvalidOperationException e) { return BadRequest(new ProblemDetails { Title = e.Message }); }
    }

    [HttpGet("transfers/recognized")]
    public async Task<IActionResult> RecognizedTransfers(CancellationToken ct)
    {
        var history = await db.TransferMatches.Where(x => x.UserId == UserId).OrderByDescending(x => x.MatchedAtUtc).ToListAsync(ct);
        var ids = history.SelectMany(x => new[] { x.OutgoingId, x.IncomingId }).ToArray();
        var entries = await db.Transactions.Include(x => x.Account).Where(x => x.UserId == UserId && ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return Ok(history.Where(h => entries.ContainsKey(h.OutgoingId) && entries.ContainsKey(h.IncomingId)
            && entries[h.OutgoingId].TransferGroupId == h.Id && entries[h.IncomingId].TransferGroupId == h.Id)
            .Select(h => new { h.Id, h.Automatic, h.MatchedAtUtc, FromAccount = entries[h.OutgoingId].Account!.Name,
                ToAccount = entries[h.IncomingId].Account!.Name, entries[h.OutgoingId].Amount, entries[h.OutgoingId].Account!.CurrencyCode }));
    }

    [HttpPost("transfers/{groupId:guid}/unlink")]
    public async Task<IActionResult> UnlinkTransfer(Guid groupId, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var history = await db.TransferMatches.SingleOrDefaultAsync(h => h.UserId == UserId && h.Id == groupId, ct);
        if (history is null) return NotFound();
        var entries = await db.Transactions.Where(t => t.UserId == UserId && t.TransferGroupId == groupId).ToListAsync(ct);
        if (entries.Count != 2 || !entries.Any(t => t.Id == history.OutgoingId) || !entries.Any(t => t.Id == history.IncomingId)) return Conflict();
        var ids = entries.Select(t => t.Id).ToArray();
        if (await db.EntryMetadata.AnyAsync(m => m.UserId == UserId && ids.Contains(m.Id) && m.Status == "Reconciled", ct)) return Conflict(new ProblemDetails { Title = "Reopen the reconciled statement before undoing this match." });
        var categories = await db.Categories.Where(c => c.UserId == UserId).Select(c => c.Id).ToListAsync(ct);
        foreach (var entry in entries)
        {
            entry.TransferGroupId = null;
            entry.Type = entry.Id == history.OutgoingId ? history.OutgoingType : history.IncomingType;
            var originalCategory = entry.Id == history.OutgoingId ? history.OutgoingCategoryId : history.IncomingCategoryId;
            entry.CategoryId = originalCategory.HasValue && categories.Contains(originalCategory.Value) ? originalCategory : null;
        }
        db.TransferMatches.Remove(history); await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return NoContent();
    }

    [HttpPost("transfers/link")]
    public async Task<IActionResult> LinkTransfer(LinkTransferRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var entries = await db.Transactions.Include(x => x.Account).Where(x => x.UserId == UserId && (x.Id == request.OutgoingId || x.Id == request.IncomingId)).ToListAsync(ct);
        var outgoing = entries.SingleOrDefault(x => x.Id == request.OutgoingId);
        var incoming = entries.SingleOrDefault(x => x.Id == request.IncomingId);
        if (outgoing is null || incoming is null) return NotFound();
        var ids = entries.Select(x => x.Id).ToList();
        if (await db.EntryMetadata.AnyAsync(x => x.UserId == UserId && ids.Contains(x.Id) && x.Status == "Reconciled", ct)) return Conflict();
        if (outgoing.Id == incoming.Id || outgoing.AccountId == incoming.AccountId || outgoing.Account!.CurrencyCode != incoming.Account!.CurrencyCode
            || outgoing.Amount != incoming.Amount || TransactionSemantics.BalanceAmount(outgoing) >= 0 || TransactionSemantics.BalanceAmount(incoming) <= 0
            || entries.Any(x => x.TransferGroupId != null || x.SplitGroupId != null || x.ParentTransactionId != null))
            return BadRequest(new ProblemDetails { Title = "Select an outgoing and incoming transaction of equal amount in different accounts with the same currency." });
        var group = Guid.NewGuid(); db.TransferMatches.Add(TransferRecognitionService.History(group, UserId, outgoing, incoming, false)); outgoing.Type = TransactionType.TransferOut; incoming.Type = TransactionType.TransferIn;
        outgoing.TransferGroupId = incoming.TransferGroupId = group; outgoing.CategoryId = incoming.CategoryId = null;
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Ok(new { TransferGroupId = group });
    }

    [HttpPost("accounts/{accountId:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(Guid accountId, ReconcileRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == accountId, ct);
        if (account is null) return NotFound();
        if (request.StatementDate == default || request.TransactionIds.Count != request.TransactionIds.Distinct().Count()) return BadRequest();
        if (await db.Reconciliations.AnyAsync(x => x.UserId == UserId && x.AccountId == accountId && x.StatementDate >= request.StatementDate, ct))
            return Conflict(new ProblemDetails { Title = "Reconcile statements in date order, or reopen the latest statement first." });
        var end = request.StatementDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        var entries = await db.Transactions.Where(x => x.UserId == UserId && x.AccountId == accountId && x.ParentTransactionId == null).ToListAsync(ct);
        var metadata = await db.EntryMetadata.Where(x => x.UserId == UserId).ToDictionaryAsync(x => x.Id, ct);
        var selected = entries.Where(x => request.TransactionIds.Contains(x.Id)).ToList();
        if (selected.Count != request.TransactionIds.Count || selected.Any(x => x.OccurredOnUtc > end || metadata.GetValueOrDefault(x.Id)?.Status == "Reconciled")) return BadRequest();
        var reconciled = entries.Where(x => metadata.GetValueOrDefault(x.Id)?.Status == "Reconciled").ToList();
        var calculated = account.OpeningBalance + reconciled.Sum(TransactionSemantics.BalanceAmount) + selected.Sum(TransactionSemantics.BalanceAmount);
        if (calculated != request.StatementBalance) return BadRequest(new { Title = "The selected transactions do not match the statement balance.", CalculatedBalance = calculated, Difference = request.StatementBalance - calculated });
        var statement = new Reconciliation { Id = Guid.NewGuid(), UserId = UserId, AccountId = accountId, StatementDate = request.StatementDate, StatementBalance = request.StatementBalance, CompletedAtUtc = DateTime.UtcNow };
        db.Reconciliations.Add(statement);
        foreach (var entry in selected)
        {
            if (!metadata.TryGetValue(entry.Id, out var m)) { m = new EntryMetadata { Id = entry.Id, UserId = UserId }; db.EntryMetadata.Add(m); }
            m.Status = "Reconciled"; m.ReconciliationId = statement.Id;
        }
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Ok(statement);
    }

    [HttpPost("reconciliations/{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var item = await db.Reconciliations.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct);
        if (item is null) return NotFound();
        if (await db.Reconciliations.AnyAsync(x => x.UserId == UserId && x.AccountId == item.AccountId && x.StatementDate > item.StatementDate, ct)) return Conflict();
        foreach (var m in await db.EntryMetadata.Where(x => x.UserId == UserId && x.ReconciliationId == id).ToListAsync(ct)) { m.Status = "Cleared"; m.ReconciliationId = null; }
        db.Reconciliations.Remove(item); await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return NoContent();
    }
}

public sealed class ProfileSettings
{
    [Required, MaxLength(120)] public string DateColumn { get; init; } = "date";
    [MaxLength(120)] public string AmountColumn { get; init; } = "amount";
    [MaxLength(120)] public string? DescriptionColumn { get; init; }
    public bool InvertAmounts { get; init; }
    [Required] public CsvImportOptions Options { get; init; } = new();
}
public sealed class ProfileRequest { [Required, MaxLength(120)] public string Name { get; init; } = ""; [Required] public ProfileSettings Settings { get; init; } = new(); }
public sealed class PayeeRequest { [Required, MaxLength(120)] public string Name { get; init; } = ""; public Guid? DefaultCategoryId { get; init; } }
public sealed class EntryRequest
{
    public Guid? PayeeId { get; init; }
    public Guid? CategoryId { get; init; }
    [Required] public string Status { get; init; } = "Uncleared";
    [Required(AllowEmptyStrings = true), MaxLength(120)] public string Reference { get; init; } = "";
    [Required(AllowEmptyStrings = true), MaxLength(400)] public string Tags { get; init; } = "";
}
public sealed class LinkTransferRequest { public Guid OutgoingId { get; init; } public Guid IncomingId { get; init; } }
public sealed class ReconcileRequest
{
    public DateOnly StatementDate { get; init; }
    public decimal StatementBalance { get; init; }
    [Required] public IReadOnlyList<Guid> TransactionIds { get; init; } = [];
}

public sealed class BulkEntryRequest { [Required] public IReadOnlyList<Guid> Ids { get; init; } = []; public Guid? PayeeId { get; init; } public Guid? CategoryId { get; init; } public string? Type { get; init; } }
