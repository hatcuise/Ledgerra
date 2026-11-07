using System.ComponentModel.DataAnnotations;
using System.Data;
using Ledgerra.Api.Extensions;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController, Authorize, Route("api/bookkeeping")]
public sealed class LedgerToolsController(LedgerraDbContext db) : ControllerBase
{
    private Guid UserId => User.GetRequiredUserId();

    [HttpGet("entries/{id:guid}/details")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var entry = await db.Transactions.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id && x.ParentTransactionId == null, ct);
        if (entry is null) return NotFound();
        var metadata = await db.EntryMetadata.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct) ?? new EntryMetadata { Id = id, UserId = UserId };
        var splits = await db.Transactions.Where(x => x.UserId == UserId && x.ParentTransactionId == id).OrderBy(x => x.Id).Select(x => new { x.CategoryId, x.Amount }).ToListAsync(ct);
        Guid? destination = entry.TransferGroupId.HasValue ? await db.Transactions.Where(x => x.UserId == UserId && x.TransferGroupId == entry.TransferGroupId && x.Id != id).Select(x => (Guid?)x.AccountId).FirstOrDefaultAsync(ct) : null;
        return Ok(new { Metadata = metadata, SplitLines = splits, DestinationAccountId = destination });
    }

    [HttpPut("payees/{id:guid}")]
    public async Task<IActionResult> UpdatePayee(Guid id, PayeeRequest request, CancellationToken ct)
    {
        var payee = await db.Payees.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct);
        if (payee is null) return NotFound();
        var name = request.Name.Trim();
        if (name.Length == 0) return BadRequest();
        if (await db.Payees.AnyAsync(x => x.UserId == UserId && x.Id != id && x.Name == name, ct)) return Conflict(new ProblemDetails { Title = "A payee already has this name. Merge them instead." });
        if (request.DefaultCategoryId.HasValue && !await db.Categories.AnyAsync(x => x.UserId == UserId && x.Id == request.DefaultCategoryId, ct)) return NotFound();
        payee.Name = name; payee.DefaultCategoryId = request.DefaultCategoryId;
        await db.SaveChangesAsync(ct); return Ok(payee);
    }

    [HttpDelete("payees/{id:guid}")]
    public async Task<IActionResult> DeletePayee(Guid id, CancellationToken ct)
    {
        var payee = await db.Payees.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct);
        if (payee is null) return NotFound();
        if (await db.EntryMetadata.AnyAsync(x => x.UserId == UserId && x.PayeeId == id, ct)) return Conflict(new ProblemDetails { Title = "This payee is used by transactions. Merge it into another payee first." });
        db.Payees.Remove(payee); await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPost("payees/{id:guid}/merge")]
    public async Task<IActionResult> MergePayee(Guid id, MergePayeeRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        if (id == request.TargetId) return BadRequest();
        var source = await db.Payees.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct);
        if (source is null || !await db.Payees.AnyAsync(x => x.UserId == UserId && x.Id == request.TargetId, ct)) return NotFound();
        var entries = await db.EntryMetadata.Where(x => x.UserId == UserId && x.PayeeId == id).ToListAsync(ct);
        foreach (var entry in entries) entry.PayeeId = request.TargetId;
        db.Payees.Remove(source); await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return Ok(new { Updated = entries.Count });
    }

    [HttpGet("tags")]
    public async Task<IActionResult> Tags(CancellationToken ct)
    {
        var values = await db.EntryMetadata.Where(x => x.UserId == UserId).Select(x => x.Tags).ToListAsync(ct);
        return Ok(values.SelectMany(SplitTags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x));
    }

    [HttpPost("tags/rename")]
    public async Task<IActionResult> RenameTag(TagRenameRequest request, CancellationToken ct)
    {
        var oldName = request.Name.Trim(); var newName = request.NewName.Trim();
        if (oldName.Length == 0 || oldName.Contains(',') || newName.Contains(',')) return BadRequest();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var entries = await db.EntryMetadata.Where(x => x.UserId == UserId && x.Tags != "").ToListAsync(ct);
        var updates = entries.Where(x => SplitTags(x.Tags).Contains(oldName, StringComparer.OrdinalIgnoreCase))
            .Select(x => (Entry: x, Tags: string.Join(", ", SplitTags(x.Tags).Select(t => t.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : t).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)))).ToList();
        if (updates.Any(x => x.Tags.Length > 400)) return BadRequest(new ProblemDetails { Title = "Renaming would exceed a transaction's tag limit." });
        foreach (var update in updates) update.Entry.Tags = update.Tags;
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Ok(new { Updated = updates.Count });
    }

    private static IEnumerable<string> SplitTags(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [HttpGet("views")]
    public async Task<IActionResult> Views(CancellationToken ct) => Ok(await db.SavedLedgerViews.Where(x => x.UserId == UserId).OrderBy(x => x.Name).ToListAsync(ct));

    [HttpPost("views")]
    public async Task<IActionResult> SaveView(SavedViewRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !IsValidViewQuery(request.Query)) return BadRequest(new ProblemDetails { Title = "Invalid saved filter name or query." });
        var name = request.Name.Trim();
        var view = await db.SavedLedgerViews.SingleOrDefaultAsync(x => x.UserId == UserId && x.Name == name, ct);
        if (view is null)
        {
            if (await db.SavedLedgerViews.CountAsync(x => x.UserId == UserId, ct) >= 100) return BadRequest(new ProblemDetails { Title = "Keep at most 100 saved filters." });
            view = new SavedLedgerView { Id = Guid.NewGuid(), UserId = UserId, Name = name }; db.SavedLedgerViews.Add(view);
        }
        view.Query = request.Query; await db.SaveChangesAsync(ct); return Ok(view);
    }

    [HttpDelete("views/{id:guid}")]
    public async Task<IActionResult> DeleteView(Guid id, CancellationToken ct)
    {
        var view = await db.SavedLedgerViews.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id, ct);
        if (view is null) return NotFound();
        db.SavedLedgerViews.Remove(view); await db.SaveChangesAsync(ct); return NoContent();
    }

    public static bool IsValidViewQuery(string? query)
    {
        if (query is null || query.Length > 4000 || query.Contains('#') || query.Contains('\n') || query.Contains('\r') || (query.Length > 0 && !query.StartsWith('?'))) return false;
        string[] allowed = ["accountId", "categoryId", "type", "from", "to", "minAmount", "maxAmount", "q", "view", "uncategorizedOnly"];
        return QueryHelpers.ParseQuery(query).All(x => allowed.Contains(x.Key) && x.Value.Count <= 100);
    }
}

public sealed class MergePayeeRequest { public Guid TargetId { get; init; } }
public sealed class SavedViewRequest
{
    [Required, MaxLength(120)] public string Name { get; init; } = "";
    [Required(AllowEmptyStrings = true), MaxLength(4000)] public string Query { get; init; } = "";
}

public sealed class TagRenameRequest
{
    [Required, MaxLength(400)] public string Name { get; init; } = "";
    [Required(AllowEmptyStrings = true), MaxLength(60)] public string NewName { get; init; } = "";
}
