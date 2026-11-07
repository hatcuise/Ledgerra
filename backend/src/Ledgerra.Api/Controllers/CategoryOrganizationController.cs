using System.Data;
using Ledgerra.Api.Extensions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController, Authorize, Route("api/bookkeeping/categories")]
public sealed class CategoryOrganizationController(LedgerraDbContext db) : ControllerBase
{
    private Guid UserId => User.GetRequiredUserId();

    [HttpPut("{id:guid}/parent")]
    public async Task<IActionResult> SetParent(Guid id, CategoryParentRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var categories = await db.Categories.Where(x => x.UserId == UserId).ToDictionaryAsync(x => x.Id, ct);
        if (!categories.TryGetValue(id, out var category)) return NotFound();
        var parent = request.ParentId; var seen = new HashSet<Guid> { id };
        while (parent.HasValue)
        {
            if (!categories.TryGetValue(parent.Value, out var item)) return NotFound();
            if (!seen.Add(item.Id) || item.Kind != category.Kind) return BadRequest(new ProblemDetails { Title = "A hierarchy must have the same category kind and cannot contain cycles." });
            parent = item.ParentCategoryId;
        }
        category.ParentCategoryId = request.ParentId; await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return NoContent();
    }

    [HttpPost("{id:guid}/merge")]
    public async Task<IActionResult> Merge(Guid id, MergePayeeRequest request, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var categories = await db.Categories.Where(x => x.UserId == UserId).ToDictionaryAsync(x => x.Id, ct);
        if (!categories.TryGetValue(id, out var source) || !categories.TryGetValue(request.TargetId, out var target)) return NotFound();
        if (id == target.Id || source.Kind != target.Kind) return BadRequest(new ProblemDetails { Title = "Choose a different category of the same kind." });
        var parent = target.ParentCategoryId; var seen = new HashSet<Guid> { target.Id };
        while (parent.HasValue)
        {
            if (parent == id || !seen.Add(parent.Value)) return BadRequest(new ProblemDetails { Title = "Move the target outside the source's descendants before merging." });
            parent = categories.GetValueOrDefault(parent.Value)?.ParentCategoryId;
        }
        var periodIds = db.BudgetPeriods.Where(x => x.UserId == UserId).Select(x => x.Id);
        var limits = await db.BudgetCategoryLimits.Where(x => periodIds.Contains(x.BudgetPeriodId) && (x.CategoryId == id || x.CategoryId == target.Id)).ToListAsync(ct);
        if (limits.GroupBy(x => x.BudgetPeriodId).Any(g => g.Count() > 1)) return Conflict(new ProblemDetails { Title = "Both categories have a limit in the same budget. Combine those limits into the target category before merging." });
        foreach (var limit in limits.Where(x => x.CategoryId == id)) limit.CategoryId = target.Id;
        foreach (var child in categories.Values.Where(x => x.ParentCategoryId == id)) child.ParentCategoryId = target.Id;
        var entries = await db.Transactions.Where(x => x.UserId == UserId && x.CategoryId == id).ToListAsync(ct);
        foreach (var entry in entries) entry.CategoryId = target.Id;
        foreach (var payee in await db.Payees.Where(x => x.UserId == UserId && x.DefaultCategoryId == id).ToListAsync(ct)) payee.DefaultCategoryId = target.Id;
        foreach (var rule in await db.CategorizationRules.Where(x => x.UserId == UserId && x.AssignCategoryId == id).ToListAsync(ct)) rule.AssignCategoryId = target.Id;
        foreach (var schedule in await db.RecurringTransactionTemplates.Where(x => x.UserId == UserId && x.CategoryId == id).ToListAsync(ct)) schedule.CategoryId = target.Id;
        foreach (var history in await db.TransferMatches.Where(x => x.UserId == UserId && (x.OutgoingCategoryId == id || x.IncomingCategoryId == id)).ToListAsync(ct))
        { if (history.OutgoingCategoryId == id) history.OutgoingCategoryId = target.Id; if (history.IncomingCategoryId == id) history.IncomingCategoryId = target.Id; }
        foreach (var view in await db.SavedLedgerViews.Where(x => x.UserId == UserId).ToListAsync(ct))
        {
            var values = QueryHelpers.ParseQuery(view.Query).SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key,
                pair.Key == "categoryId" && Guid.TryParse(value, out var categoryId) && categoryId == id ? target.Id.ToString() : value))).Distinct().ToList();
            view.Query = QueryString.Create(values).Value ?? "";
        }
        // Repair dependencies before deleting the source, including restrictive foreign keys.
        await db.SaveChangesAsync(ct); db.Categories.Remove(source); await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct); return Ok(new { UpdatedEntries = entries.Count });
    }
}
public sealed class CategoryParentRequest { public Guid? ParentId { get; init; } }
