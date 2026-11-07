using System.ComponentModel.DataAnnotations;

namespace Ledgerra.Api.Contracts;

public sealed record MonthlyReportAnalysisResponse(
    IReadOnlyList<MonthlyReportDraftTransactionResponse> Transactions,
    IReadOnlyList<string> Warnings);

public sealed record MonthlyReportAnalysisJobResponse(
    Guid JobId,
    string Status,
    string? StatusMessage,
    int? GeneratedOutputCharacters,
    MonthlyReportAnalysisTokenUsageResponse? Usage,
    MonthlyReportAnalysisResponse? Analysis,
    string? Error,
    bool HasRawAiOutput,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record MonthlyReportAnalysisTokenUsageResponse(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record MonthlyReportDraftTransactionResponse(
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
    string? ExternalKey = null);

public sealed class CommitMonthlyReportDraftsRequest
{
    [Required, MinLength(1)]
    public IReadOnlyList<CommitMonthlyReportDraftRequest> Transactions { get; init; } = [];

    public IReadOnlyList<string> AcceptedDuplicateSourceIds { get; init; } = [];
}

public sealed class CommitMonthlyReportDraftRequest : IValidatableObject
{
    [Required, MaxLength(120)]
    public string SourceId { get; init; } = string.Empty;

    [Required]
    public Guid AccountId { get; init; }

    public Guid? CategoryId { get; init; }

    [Range(0.01, 999999999)]
    public decimal Amount { get; init; }

    [Required]
    public string Type { get; init; } = string.Empty;

    [Required]
    public DateTime OccurredOnUtc { get; init; }

    [MaxLength(400)]
    public string? Note { get; init; }

    [MaxLength(120)] public string? PayeeName { get; init; }
    [MaxLength(120)] public string? Reference { get; init; }
    [MaxLength(400)] public string? Tags { get; init; }
    [RegularExpression("^[0-9A-Fa-f]{64}$")] public string? ExternalKey { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AccountId == Guid.Empty)
        {
            yield return new ValidationResult("AccountId must not be empty.", [nameof(AccountId)]);
        }

        if (OccurredOnUtc == default || OccurredOnUtc == DateTime.MinValue)
        {
            yield return new ValidationResult("OccurredOnUtc must be set.", [nameof(OccurredOnUtc)]);
        }
        else if (OccurredOnUtc.Kind != DateTimeKind.Utc)
        {
            yield return new ValidationResult("OccurredOnUtc must be a UTC date/time.", [nameof(OccurredOnUtc)]);
        }
    }
}

public sealed record CommitMonthlyReportDraftsResponse(IReadOnlyList<TransactionResponse> Created);

public sealed class CsvImportPreviewRequest
{
    [Required]
    public IFormFile File { get; init; } = null!;

    [Required]
    public Guid AccountId { get; init; }

    [Required, MaxLength(120)]
    public string DateColumn { get; init; } = string.Empty;

    [Required, MaxLength(120)]
    public string AmountColumn { get; init; } = string.Empty;

    public string Delimiter { get; init; } = ",";
    public string CultureName { get; init; } = "en-US";
    public string? DateFormat { get; init; }
    public string? DebitColumn { get; init; }
    public string? CreditColumn { get; init; }
    public string? TypeColumn { get; init; }
    public string? PayeeColumn { get; init; }
    public string? CategoryColumn { get; init; }
    public string? ReferenceColumn { get; init; }
    public string? TagsColumn { get; init; }
    public string PositiveType { get; init; } = "Income";
    public string NegativeType { get; init; } = "Expense";
    public bool InvertAmounts { get; init; }

    [MaxLength(120)]
    public string? DescriptionColumn { get; init; }
}
