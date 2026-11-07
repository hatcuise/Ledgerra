using Ledgerra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Ledgerra.Application.Imports;

namespace Ledgerra.Api.Services.Imports;

public sealed class MonthlyReportDuplicateReviewerAdapter : IMonthlyReportDuplicateReviewer
{
    private readonly IImportDuplicateDetector _duplicateDetector;
    private readonly LedgerraDbContext _db;

    public MonthlyReportDuplicateReviewerAdapter(IImportDuplicateDetector duplicateDetector, LedgerraDbContext db)
    {
        _duplicateDetector = duplicateDetector;
        _db = db;
    }

    public async Task<IReadOnlyList<MonthlyReportDraftDuplicateReview>> MarkDuplicatesAsync(
        Guid userId,
        IReadOnlyList<MonthlyReportDraftInput> drafts,
        CancellationToken cancellationToken)
    {
        var reviewedDrafts = await _duplicateDetector.MarkDuplicatesAsync(
            userId,
            drafts.Select(draft => ImportDraftReviewItem.FromAnalyzedDraft(
                draft.SourceId,
                draft.AccountId,
                draft.CategoryId,
                draft.Amount,
                draft.Type,
                draft.OccurredOnUtc.ToUniversalTime(),
                draft.Note,
                1m,
                [])).ToList(),
            cancellationToken);

        var keys = drafts.Where(d => d.ExternalKey != null).Select(d => d.ExternalKey!).ToList();
        var existing = (await _db.EntryMetadata.Where(m => m.UserId == userId && m.ExternalKey != null && keys.Contains(m.ExternalKey)).Select(m => m.ExternalKey!).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var source = drafts.ToDictionary(d => d.SourceId);
        return reviewedDrafts.Select(d => {
            var key = source[d.SourceId].ExternalKey;
            var exact = key != null && (existing.Contains(key) || counts[key] > 1);
            return new MonthlyReportDraftDuplicateReview(d.SourceId, exact || d.IsLikelyDuplicate, exact);
        }).ToList();
    }
}