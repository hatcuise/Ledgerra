using Ledgerra.Domain.Ai;

namespace Ledgerra.Application.Imports;

public sealed record AnalyzeMonthlyReportCommand(
    Guid UserId,
    Guid AccountId,
    string Month,
    AiProvider Provider,
    string ReportContent,
    IProgress<MonthlyReportAnalyzerProgress>? Progress = null);

public sealed record MonthlyReportAnalyzerTokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record MonthlyReportAnalyzerProgress(
    string StatusMessage,
    int? GeneratedOutputCharacters = null,
    MonthlyReportAnalyzerTokenUsage? Usage = null);

public sealed record AnalyzedMonthlyReportDraft(
    string SourceId,
    Guid AccountId,
    Guid? CategoryId,
    decimal Amount,
    string Type,
    DateTime OccurredOnUtc,
    string? Note,
    decimal Confidence,
    IReadOnlyList<string> Warnings,
    Guid? AppliedRuleId,
    string? AppliedRuleName,
    bool IsLikelyDuplicate,
    Guid? DuplicateTransactionId,
    string? DuplicateReason,
    bool IsSelectedByDefault,
    string? PayeeName = null,
    string? Reference = null,
    string? Tags = null,
    string? CategoryName = null);

public sealed record AnalyzeMonthlyReportResult(
    IReadOnlyList<AnalyzedMonthlyReportDraft> Transactions,
    IReadOnlyList<string> Warnings,
    MonthlyReportAnalyzerTokenUsage? Usage = null);

public sealed record AiDraftAnalysisItem(
    string SourceId,
    string AccountId,
    string? CategoryId,
    decimal Amount,
    string Type,
    string OccurredOnUtc,
    string? Note,
    decimal Confidence,
    IReadOnlyList<string> Warnings);

public sealed record AiDraftAnalysisResult(
    IReadOnlyList<AiDraftAnalysisItem> Transactions,
    IReadOnlyList<string> Warnings,
    MonthlyReportAnalyzerTokenUsage? Usage = null);

public sealed record MonthlyReportReviewDraft(
    string SourceId,
    Guid AccountId,
    Guid? CategoryId,
    decimal Amount,
    string Type,
    DateTime OccurredOnUtc,
    string? Note,
    decimal Confidence,
    IReadOnlyList<string> Warnings,
    Guid? AppliedRuleId,
    string? AppliedRuleName,
    bool IsLikelyDuplicate,
    Guid? DuplicateTransactionId,
    string? DuplicateReason,
    bool IsSelectedByDefault);

public interface IMonthlyReportAnalyzer
{
    Task<AiDraftAnalysisResult> AnalyzeAsync(
        Guid userId,
        Guid accountId,
        AiProvider provider,
        string month,
        string reportContent,
        CancellationToken cancellationToken,
        IProgress<MonthlyReportAnalyzerProgress>? progress = null);
}

public interface IMonthlyReportRuleMatcher
{
    Task<IReadOnlyList<MonthlyReportReviewDraft>> ApplyAsync(
        Guid userId,
        IReadOnlyList<MonthlyReportReviewDraft> drafts,
        CancellationToken cancellationToken);
}

public interface IMonthlyReportDuplicateMarker
{
    Task<IReadOnlyList<MonthlyReportReviewDraft>> MarkDuplicatesAsync(
        Guid userId,
        IReadOnlyList<MonthlyReportReviewDraft> drafts,
        CancellationToken cancellationToken);
}

public sealed class AnalyzeMonthlyReportCommandHandler
{
    private readonly IMonthlyReportAnalyzer _monthlyReportAnalyzer;
    private readonly IMonthlyReportRuleMatcher _monthlyReportRuleMatcher;
    private readonly IMonthlyReportDuplicateMarker _monthlyReportDuplicateMarker;

    public AnalyzeMonthlyReportCommandHandler(
        IMonthlyReportAnalyzer monthlyReportAnalyzer,
        IMonthlyReportRuleMatcher monthlyReportRuleMatcher,
        IMonthlyReportDuplicateMarker monthlyReportDuplicateMarker)
    {
        _monthlyReportAnalyzer = monthlyReportAnalyzer;
        _monthlyReportRuleMatcher = monthlyReportRuleMatcher;
        _monthlyReportDuplicateMarker = monthlyReportDuplicateMarker;
    }

    public async Task<AnalyzeMonthlyReportResult> HandleAsync(AnalyzeMonthlyReportCommand command, CancellationToken cancellationToken)
    {
        var result = await _monthlyReportAnalyzer.AnalyzeAsync(
            command.UserId,
            command.AccountId,
            command.Provider,
            command.Month,
            command.ReportContent,
            cancellationToken,
            command.Progress);

        return await HandleAnalysisResultAsync(command, result, cancellationToken);
    }

    public async Task<AnalyzeMonthlyReportResult> HandleAnalysisResultAsync(
        AnalyzeMonthlyReportCommand command,
        AiDraftAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var analyzedDrafts = new List<MonthlyReportReviewDraft>();
        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedCommandAccountId = command.AccountId;

        foreach (var transaction in result.Transactions)
        {
            if ((transaction.CategoryId is not null && !Guid.TryParse(transaction.CategoryId, out _)) ||
                !DateTime.TryParse(transaction.OccurredOnUtc, out var parsedOccurredOnUtc) ||
                string.IsNullOrWhiteSpace(transaction.SourceId) ||
                !sourceIds.Add(transaction.SourceId))
            {
                throw new InvalidOperationException("AI report analysis returned a malformed transaction draft.");
            }

            if (!string.IsNullOrWhiteSpace(transaction.AccountId) &&
                (!Guid.TryParse(transaction.AccountId, out var parsedAiAccountId) || parsedAiAccountId != parsedCommandAccountId))
            {
                throw new InvalidOperationException("AI report analysis returned a transaction draft for a different account.");
            }

            var parsedCategoryId = transaction.CategoryId is null ? (Guid?)null : Guid.Parse(transaction.CategoryId);
            analyzedDrafts.Add(new MonthlyReportReviewDraft(
                transaction.SourceId,
                parsedCommandAccountId,
                parsedCategoryId,
                transaction.Amount,
                transaction.Type,
                parsedOccurredOnUtc.ToUniversalTime(),
                transaction.Note,
                transaction.Confidence,
                transaction.Warnings,
                null,
                null,
                false,
                null,
                null,
                true));
        }

        var categorizedDrafts = await _monthlyReportRuleMatcher.ApplyAsync(command.UserId, analyzedDrafts, cancellationToken);
        var reviewedDrafts = await _monthlyReportDuplicateMarker.MarkDuplicatesAsync(command.UserId, categorizedDrafts, cancellationToken);

        return new AnalyzeMonthlyReportResult(
            reviewedDrafts.Select(draft => new AnalyzedMonthlyReportDraft(
                draft.SourceId,
                draft.AccountId,
                draft.CategoryId,
                draft.Amount,
                draft.Type,
                draft.OccurredOnUtc,
                draft.Note,
                draft.Confidence,
                draft.Warnings,
                draft.AppliedRuleId,
                draft.AppliedRuleName,
                draft.IsLikelyDuplicate,
                draft.DuplicateTransactionId,
                draft.DuplicateReason,
                draft.IsSelectedByDefault)).ToList(),
            result.Warnings,
            result.Usage);
    }
}
