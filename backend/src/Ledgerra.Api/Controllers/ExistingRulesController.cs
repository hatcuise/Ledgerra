using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ledgerra.Api.Extensions;
using Ledgerra.Api.Services.Imports;
using Ledgerra.Application.Reporting;
using Ledgerra.Domain.Transactions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController, Authorize, Route("api/bookkeeping/rules")]
public sealed class ExistingRulesController(LedgerraDbContext db, IImportCategorizationRuleMatcher matcher, IMonthlyAccountBalanceSnapshotService snapshots) : ControllerBase
{
    private Guid UserId => User.GetRequiredUserId();

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(ExistingRulePreviewRequest request, CancellationToken ct)
    {
        if (request.From > request.To) return BadRequest();
        var rows = await Changes(request, ct);
        return rows is null ? BadRequest(new ProblemDetails { Title = "Choose a smaller date range (at most 2,000 eligible transactions)." }) : Ok(rows);
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(ExistingRuleApplyRequest request, CancellationToken ct)
    {
        if (request.From > request.To || request.Selected.Count is < 1 or > 2000 || request.Selected.Select(x => x.Id).Distinct().Count() != request.Selected.Count) return BadRequest();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var changes = await Changes(new ExistingRulePreviewRequest { From = request.From, To = request.To }, ct);
        var byId = changes?.ToDictionary(x => x.Id);
        if (byId is null || request.Selected.Any(x => !byId.TryGetValue(x.Id, out var row) || row.Fingerprint != x.Fingerprint))
            return Conflict(new ProblemDetails { Title = "Transactions or rules changed after preview. Review a fresh preview before applying." });
        var ids = request.Selected.Select(x => x.Id).ToArray();
        var entries = await db.Transactions.Where(x => x.UserId == UserId && ids.Contains(x.Id)).ToListAsync(ct);
        foreach (var entry in entries) { entry.CategoryId = byId[entry.Id].NewCategoryId; entry.Type = Enum.Parse<TransactionType>(byId[entry.Id].NewType); }
        await db.SaveChangesAsync(ct);
        foreach (var group in entries.GroupBy(x => x.AccountId))
        { var date = group.Min(x => x.OccurredOnUtc); await snapshots.RefreshFromAsync(UserId, new DateOnly(date.Year, date.Month, 1), group.Key, ct); }
        if (tx is not null) await tx.CommitAsync(ct); return Ok(new { Updated = entries.Count });
    }

    private async Task<List<ExistingRuleChange>?> Changes(ExistingRulePreviewRequest request, CancellationToken ct)
    {
        var from = request.From?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = request.To?.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        var entries = await db.Transactions.Where(x => x.UserId == UserId && x.ParentTransactionId == null && x.SplitGroupId == null && x.TransferGroupId == null
            && (x.Type == TransactionType.Expense || x.Type == TransactionType.Income || x.Type == TransactionType.Refund)
            && (!from.HasValue || x.OccurredOnUtc >= from) && (!to.HasValue || x.OccurredOnUtc <= to)
            && !db.EntryMetadata.Any(m => m.UserId == UserId && m.Id == x.Id && m.Status == "Reconciled")).OrderBy(x => x.Id).Take(2001).ToListAsync(ct);
        if (entries.Count > 2000) return null;
        var originals = entries.ToDictionary(x => x.Id);
        var drafts = entries.Select(x => ImportDraftReviewItem.FromAnalyzedDraft(x.Id.ToString(), x.AccountId, x.CategoryId, x.Amount, x.Type.ToString(), x.OccurredOnUtc, x.Note, 1, [])).ToList();
        var reviewed = await matcher.ApplyAsync(UserId, drafts, ct);
        return reviewed.Where(x => x.AppliedRuleId.HasValue).Select(x => (Draft: x, Old: originals[Guid.Parse(x.SourceId)]))
            .Where(x => x.Draft.CategoryId != x.Old.CategoryId || x.Draft.Type != x.Old.Type.ToString())
            .Select(x => new ExistingRuleChange(x.Old.Id, x.Old.Note, x.Old.Amount, x.Old.OccurredOnUtc, x.Old.CategoryId, x.Old.Type.ToString(), x.Draft.CategoryId!.Value, x.Draft.Type, x.Draft.AppliedRuleName!,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { UserId, x.Old.Id, x.Old.AccountId, x.Old.Amount, x.Old.CategoryId, x.Old.Type, x.Old.Note, x.Old.OccurredOnUtc, NewCategory = x.Draft.CategoryId, NewType = x.Draft.Type, Rule = x.Draft.AppliedRuleId })))))).ToList();
    }
}
public sealed class ExistingRulePreviewRequest { public DateOnly? From { get; init; } public DateOnly? To { get; init; } }
public sealed class ExistingRuleApplyRequest
{
    public DateOnly? From { get; init; } public DateOnly? To { get; init; }
    [Required] public IReadOnlyList<ExistingRuleSelection> Selected { get; init; } = [];
}
public sealed record ExistingRuleSelection(Guid Id, string Fingerprint);
public sealed record ExistingRuleChange(Guid Id, string? Note, decimal Amount, DateTime OccurredOnUtc, Guid? OldCategoryId, string OldType, Guid NewCategoryId, string NewType, string RuleName, string Fingerprint);
