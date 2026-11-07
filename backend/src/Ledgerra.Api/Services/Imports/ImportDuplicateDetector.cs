using Ledgerra.Domain.Transactions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Services.Imports;

public sealed class ImportDuplicateDetector : IImportDuplicateDetector
{
    private readonly LedgerraDbContext _dbContext;

    public ImportDuplicateDetector(LedgerraDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ImportDraftReviewItem>> MarkDuplicatesAsync(
        Guid userId,
        IReadOnlyList<ImportDraftReviewItem> drafts,
        CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
        {
            return drafts;
        }

        var accountIds = drafts.Select(draft => draft.AccountId).Distinct().ToList();
        var amounts = drafts.Select(draft => draft.Amount).Distinct().ToList();
        var types = drafts
            .Select(draft => ParseIncomeExpenseType(draft.Type))
            .OfType<TransactionType>()
            .Distinct()
            .ToList();
        if (types.Contains(TransactionType.Income)) types.Add(TransactionType.TransferIn);
        if (types.Contains(TransactionType.Expense)) types.Add(TransactionType.TransferOut);
        var minDate = drafts.Min(draft => draft.OccurredOnUtc.ToUniversalTime().Date);
        var maxExclusiveDate = drafts.Max(draft => draft.OccurredOnUtc.ToUniversalTime().Date).AddDays(1);

        if (types.Count == 0)
        {
            return drafts;
        }

        var existing = await _dbContext.Transactions
            .Where(transaction =>
                transaction.UserId == userId &&
                accountIds.Contains(transaction.AccountId) &&
                amounts.Contains(transaction.Amount) &&
                types.Contains(transaction.Type) &&
                transaction.OccurredOnUtc >= minDate &&
                transaction.OccurredOnUtc < maxExclusiveDate)
            .ToListAsync(cancellationToken);

        var seen = new HashSet<(Guid, DateTime, decimal, string, string)>();
        return drafts.Select(draft =>
        {
            var marked = MarkDuplicate(draft, existing);
            var key = (draft.AccountId, draft.OccurredOnUtc.ToUniversalTime().Date, draft.Amount, draft.Type.ToUpperInvariant(), NormalizeNote(draft.Note ?? ""));
            if (!seen.Add(key) && !marked.IsLikelyDuplicate)
                marked = marked with { IsLikelyDuplicate = true, DuplicateReason = "Matches an earlier row in this import.", IsSelectedByDefault = false };
            return marked;
        }).ToList();
    }

    private static ImportDraftReviewItem MarkDuplicate(ImportDraftReviewItem draft, IReadOnlyList<Transaction> existing)
    {
        var draftDate = draft.OccurredOnUtc.ToUniversalTime().Date;
        var draftType = ParseIncomeExpenseType(draft.Type);
        if (draftType is null)
        {
            return draft;
        }

        var duplicate = existing.FirstOrDefault(transaction =>
            transaction.AccountId == draft.AccountId &&
            (transaction.Type == draftType.Value || (draftType.Value == TransactionType.Income && transaction.Type == TransactionType.TransferIn) || (draftType.Value == TransactionType.Expense && transaction.Type == TransactionType.TransferOut)) &&
            transaction.Amount == draft.Amount &&
            transaction.OccurredOnUtc.ToUniversalTime().Date == draftDate &&
            NotesMatch(transaction.Note, draft.Note));

        if (duplicate is null)
        {
            return draft;
        }

        return draft with
        {
            IsLikelyDuplicate = true,
            DuplicateTransactionId = duplicate.Id,
            DuplicateReason = "Matches an existing transaction with the same account, date, type, amount, and note.",
            IsSelectedByDefault = false
        };
    }

    private static TransactionType? ParseIncomeExpenseType(string type)
    {
        if (!Enum.TryParse<TransactionType>(type, true, out var parsedType))
        {
            return null;
        }

        return parsedType is TransactionType.Income or TransactionType.Expense or TransactionType.Refund ? parsedType : null;
    }

    private static bool NotesMatch(string? existingNote, string? draftNote)
    {
        var existingNoteIsBlank = string.IsNullOrWhiteSpace(existingNote);
        var draftNoteIsBlank = string.IsNullOrWhiteSpace(draftNote);
        if (existingNoteIsBlank && draftNoteIsBlank)
        {
            return true;
        }

        if (existingNoteIsBlank || draftNoteIsBlank)
        {
            return false;
        }

        return NormalizeNote(existingNote!) == NormalizeNote(draftNote!);
    }

    private static string NormalizeNote(string note)
    {
        return string.Join(' ', note.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
