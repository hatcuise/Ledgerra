using Ledgerra.Application.Reporting;
using Ledgerra.Application.Transactions;
using Ledgerra.Domain.Transactions;

namespace Ledgerra.Application.Imports;

public sealed record CommitMonthlyReportDraftsCommand(
    Guid UserId,
    IReadOnlyList<MonthlyReportDraftInput> Transactions,
    IReadOnlyList<string> AcceptedDuplicateSourceIds);

public sealed record MonthlyReportDraftInput(
    string SourceId,
    Guid AccountId,
    Guid? CategoryId,
    decimal Amount,
    string Type,
    DateTime OccurredOnUtc,
    string? Note,
    string? PayeeName = null,
    string? Reference = null,
    string? Tags = null,
    string? ExternalKey = null);

public sealed record MonthlyReportDraftDuplicateReview(string SourceId, bool IsLikelyDuplicate, bool IdentityDuplicate = false);

public interface IMonthlyReportImportCommitStore
{
    Task<bool> AccountExistsAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);

    Task<bool> CategoryExistsAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Transaction>> CreateTransactionsAsync(
        Guid userId,
        IReadOnlyList<MonthlyReportDraftInput> drafts,
        CancellationToken cancellationToken);
}

public interface IMonthlyReportDuplicateReviewer
{
    Task<IReadOnlyList<MonthlyReportDraftDuplicateReview>> MarkDuplicatesAsync(
        Guid userId,
        IReadOnlyList<MonthlyReportDraftInput> drafts,
        CancellationToken cancellationToken);
}

public sealed class CommitMonthlyReportDraftsCommandHandler
{
    private readonly IMonthlyReportDuplicateReviewer _duplicateReviewer;
    private readonly IMonthlyReportImportCommitStore _store;
    private readonly IMonthlyAccountBalanceSnapshotService? _snapshots;

    public CommitMonthlyReportDraftsCommandHandler(
        IMonthlyReportImportCommitStore store,
        IMonthlyReportDuplicateReviewer duplicateReviewer,
        IMonthlyAccountBalanceSnapshotService? snapshots = null)
    {
        _store = store;
        _duplicateReviewer = duplicateReviewer;
        _snapshots = snapshots;
    }

    public async Task<CommitMonthlyReportDraftsResult> HandleAsync(
        CommitMonthlyReportDraftsCommand command,
        CancellationToken cancellationToken)
    {
        var sourceIdValidation = ValidateSourceIds(command.Transactions);
        if (sourceIdValidation is not null)
        {
            return sourceIdValidation;
        }

        var acceptedDuplicateSourceIdValidation = ValidateAcceptedDuplicateSourceIds(command.AcceptedDuplicateSourceIds);
        if (acceptedDuplicateSourceIdValidation is not null)
        {
            return acceptedDuplicateSourceIdValidation;
        }

        foreach (var draft in command.Transactions)
        {
            var validation = await ValidateDraftAsync(command.UserId, draft, cancellationToken);
            if (validation is not null)
            {
                return validation;
            }
        }

        var acceptedDuplicateSourceIds = command.AcceptedDuplicateSourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewedDrafts = await _duplicateReviewer.MarkDuplicatesAsync(command.UserId, command.Transactions, cancellationToken);

        if (reviewedDrafts.Any(d => d.IdentityDuplicate))
            return CommitMonthlyReportDraftsResult.ValidationError("externalKey", "A bank transaction identity was already imported or repeated in this batch. Remove the duplicate rows.");

        var unacceptedDuplicate = reviewedDrafts.FirstOrDefault(draft =>
            draft.IsLikelyDuplicate && !acceptedDuplicateSourceIds.Contains(draft.SourceId));

        if (unacceptedDuplicate is not null)
        {
            return CommitMonthlyReportDraftsResult.ValidationError(
                "duplicates",
                $"Draft {unacceptedDuplicate.SourceId} appears to duplicate an existing transaction.");
        }

        var transactions = await _store.CreateTransactionsAsync(command.UserId, command.Transactions, cancellationToken);

        if (_snapshots is not null)
            foreach (var group in transactions.GroupBy(x => x.AccountId))
            {
                var earliest = group.Min(x => x.OccurredOnUtc);
                await _snapshots.RefreshFromAsync(command.UserId, new DateOnly(earliest.Year, earliest.Month, 1), group.Key, cancellationToken);
            }
        return CommitMonthlyReportDraftsResult.Success(transactions.Select(MapTransaction).ToList());
    }

    private CommitMonthlyReportDraftsResult? ValidateSourceIds(IReadOnlyList<MonthlyReportDraftInput> drafts)
    {
        if (drafts.Any(draft => string.IsNullOrWhiteSpace(draft.SourceId)))
        {
            return CommitMonthlyReportDraftsResult.ValidationError(
                "sourceId",
                "Imported report draft sourceId must be set.");
        }

        var duplicateSourceId = drafts
            .GroupBy(draft => draft.SourceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateSourceId is not null)
        {
            return CommitMonthlyReportDraftsResult.ValidationError(
                "sourceId",
                $"Imported report draft sourceId '{duplicateSourceId.Key}' must be unique within the request.");
        }

        return null;
    }

    private CommitMonthlyReportDraftsResult? ValidateAcceptedDuplicateSourceIds(IReadOnlyList<string> sourceIds)
    {
        if (sourceIds.Any(string.IsNullOrWhiteSpace))
        {
            return CommitMonthlyReportDraftsResult.ValidationError(
                "acceptedDuplicateSourceIds",
                "Accepted duplicate source ids must not be blank.");
        }

        var duplicateSourceId = sourceIds
            .GroupBy(sourceId => sourceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateSourceId is not null)
        {
            return CommitMonthlyReportDraftsResult.ValidationError(
                "acceptedDuplicateSourceIds",
                $"Accepted duplicate source id '{duplicateSourceId.Key}' must be unique within the request.");
        }

        return null;
    }

    private async Task<CommitMonthlyReportDraftsResult?> ValidateDraftAsync(
        Guid userId,
        MonthlyReportDraftInput draft,
        CancellationToken cancellationToken)
    {
        var accountExists = await _store.AccountExistsAsync(userId, draft.AccountId, cancellationToken);
        if (!accountExists)
        {
            return CommitMonthlyReportDraftsResult.NotFound("Account not found");
        }

        if (draft.Amount <= 0)
        {
            return CommitMonthlyReportDraftsResult.ValidationError(
                "amount",
                "Imported report draft amount must be positive.");
        }

        if (!TryParseSupportedTransactionType(draft.Type, out _))
        {
            return CommitMonthlyReportDraftsResult.ValidationError(
                "type",
                "Imported report drafts must be Income, Expense, or Refund.");
        }

        if (draft.CategoryId.HasValue)
        {
            var categoryExists = await _store.CategoryExistsAsync(userId, draft.CategoryId.Value, cancellationToken);
            if (!categoryExists)
            {
                return CommitMonthlyReportDraftsResult.NotFound("Category not found");
            }
        }

        return null;
    }

    private static bool TryParseSupportedTransactionType(string type, out TransactionType parsedType)
    {
        if (Enum.TryParse<TransactionType>(type, true, out parsedType) &&
            (parsedType == TransactionType.Income || parsedType == TransactionType.Expense || parsedType == TransactionType.Refund))
        {
            return true;
        }

        parsedType = default;
        return false;
    }

    private static TransactionDetails MapTransaction(Transaction transaction)
    {
        return new TransactionDetails(
            transaction.Id,
            transaction.AccountId,
            transaction.CategoryId,
            transaction.Amount,
            transaction.Type.ToString(),
            transaction.OccurredOnUtc,
            transaction.Note,
            transaction.TransferGroupId,
            transaction.SavingsGoalId,
            transaction.SplitGroupId,
            transaction.ParentTransactionId);
    }
}

public sealed class CommitMonthlyReportDraftsResult
{
    private CommitMonthlyReportDraftsResult(
        IReadOnlyList<TransactionDetails>? created,
        string? notFoundTitle,
        string? validationKey,
        string? validationMessage)
    {
        Created = created;
        NotFoundTitle = notFoundTitle;
        ValidationKey = validationKey;
        ValidationMessage = validationMessage;
    }

    public IReadOnlyList<TransactionDetails>? Created { get; }

    public string? NotFoundTitle { get; }

    public string? ValidationKey { get; }

    public string? ValidationMessage { get; }

    public bool IsNotFound => NotFoundTitle is not null;

    public bool HasValidationError => ValidationKey is not null;

    public static CommitMonthlyReportDraftsResult Success(IReadOnlyList<TransactionDetails> created) => new(created, null, null, null);

    public static CommitMonthlyReportDraftsResult NotFound(string title) => new(null, title, null, null);

    public static CommitMonthlyReportDraftsResult ValidationError(string key, string message) => new(null, null, key, message);
}
