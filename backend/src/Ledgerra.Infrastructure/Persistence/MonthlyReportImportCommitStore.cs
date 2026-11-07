using System.Data;
using Ledgerra.Application.Imports;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Domain.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public sealed class MonthlyReportImportCommitStore : IMonthlyReportImportCommitStore
{
    private readonly LedgerraDbContext _dbContext;
    private readonly TransferRecognitionService _transfers;

    public MonthlyReportImportCommitStore(LedgerraDbContext dbContext, TransferRecognitionService transfers)
    {
        _dbContext = dbContext;
        _transfers = transfers;
    }

    public Task<bool> AccountExistsAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        return _dbContext.Accounts.AnyAsync(item => item.UserId == userId && item.Id == accountId, cancellationToken);
    }

    public Task<bool> CategoryExistsAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        return _dbContext.Categories.AnyAsync(item => item.UserId == userId && item.Id == categoryId, cancellationToken);
    }

    public async Task<IReadOnlyList<Transaction>> CreateTransactionsAsync(
        Guid userId,
        IReadOnlyList<MonthlyReportDraftInput> drafts,
        CancellationToken cancellationToken)
    {
        await using var tx = _dbContext.Database.IsRelational() && _dbContext.Database.CurrentTransaction is null ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        var transactions = new List<Transaction>(drafts.Count);
        var payees = (await _dbContext.Payees.Where(x => x.UserId == userId).ToListAsync(cancellationToken)).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts)
        {
            Enum.TryParse<TransactionType>(draft.Type, true, out var parsedType);
            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = draft.AccountId,
                CategoryId = draft.CategoryId,
                Amount = draft.Amount,
                Type = parsedType,
                Note = draft.Note,
                OccurredOnUtc = draft.OccurredOnUtc.ToUniversalTime()
            };
            transactions.Add(transaction);
            Payee? payee = null;
            if (!string.IsNullOrWhiteSpace(draft.PayeeName))
            {
                var name = draft.PayeeName.Trim();
                if (!payees.TryGetValue(name, out payee))
                {
                    payee = new Payee { Id = Guid.NewGuid(), UserId = userId, Name = name };
                    payees.Add(name, payee); _dbContext.Payees.Add(payee);
                }
            }
            if (draft.ExternalKey is not null || payee is not null || !string.IsNullOrEmpty(draft.Reference) || !string.IsNullOrEmpty(draft.Tags))
                _dbContext.EntryMetadata.Add(new EntryMetadata { Id = transaction.Id, UserId = userId, ExternalKey = draft.ExternalKey?.ToUpperInvariant(), PayeeId = payee?.Id, Reference = draft.Reference ?? "", Tags = draft.Tags ?? "" });
        }

        _dbContext.Transactions.AddRange(transactions);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _transfers.RecognizeAsync(userId, transactions.Select(t => t.Id).ToArray(), ct: cancellationToken);
        if (tx is not null) await tx.CommitAsync(cancellationToken);
        return transactions;
    }
}