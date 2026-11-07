using Ledgerra.Application.Transactions;
using Ledgerra.Domain.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public sealed class TransactionCommandStore : ITransactionCommandStore
{
    private readonly LedgerraDbContext _dbContext;

    public TransactionCommandStore(LedgerraDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> AccountExistsAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        return _dbContext.Accounts.AnyAsync(item => item.UserId == userId && item.Id == accountId, cancellationToken);
    }

    public async Task<bool> AccountsShareCurrencyAsync(Guid userId, Guid sourceAccountId, Guid destinationAccountId, CancellationToken cancellationToken)
    {
        var currencies = await _dbContext.Accounts.Where(x => x.UserId == userId && (x.Id == sourceAccountId || x.Id == destinationAccountId)).Select(x => x.CurrencyCode).ToListAsync(cancellationToken);
        return currencies.Count == 2 && currencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
    }

    public Task<bool> CategoryExistsAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        return _dbContext.Categories.AnyAsync(item => item.UserId == userId && item.Id == categoryId, cancellationToken);
    }

    public Task<Transaction?> GetByIdAsync(Guid userId, Guid transactionId, CancellationToken cancellationToken)
    {
        return _dbContext.Transactions.SingleOrDefaultAsync(item => item.UserId == userId && item.Id == transactionId, cancellationToken);
    }

    public async Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        if (transaction.SplitGroupId.HasValue)
        {
            var linkedTransactions = await _dbContext.Transactions
                .Where(item => item.UserId == transaction.UserId && item.SplitGroupId == transaction.SplitGroupId.Value)
                .ToListAsync(cancellationToken);

            _dbContext.Transactions.RemoveRange(linkedTransactions);
        }
        else
        {
            _dbContext.Transactions.Remove(transaction);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTransferGroupAsync(Guid userId, Guid transferGroupId, CancellationToken cancellationToken)
    {
        var linkedTransactions = await _dbContext.Transactions
            .Where(item => item.UserId == userId && item.TransferGroupId == transferGroupId)
            .ToListAsync(cancellationToken);

        _dbContext.Transactions.RemoveRange(linkedTransactions);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Transaction> MoveToAccountAsync(
        Transaction existing,
        Guid destinationAccountId,
        CancellationToken cancellationToken)
    {
        if (existing.SplitGroupId.HasValue)
        {
            var linkedTransactions = await _dbContext.Transactions
                .Where(item => item.UserId == existing.UserId && item.SplitGroupId == existing.SplitGroupId.Value)
                .ToListAsync(cancellationToken);

            foreach (var transaction in linkedTransactions)
            {
                transaction.AccountId = destinationAccountId;
            }
        }
        else
        {
            existing.AccountId = destinationAccountId;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<Transaction> CreateAsync(
        Transaction transaction,
        CancellationToken cancellationToken,
        IReadOnlyList<TransactionSplitLine>? splitLines = null)
    {
        EnsureUtc(transaction.OccurredOnUtc);

        var splitTransactions = BuildSplitTransactions(transaction, splitLines);
        _dbContext.Transactions.Add(transaction);
        if (splitTransactions.Count > 0)
        {
            _dbContext.Transactions.AddRange(splitTransactions);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return transaction;
    }

    public async Task<Transaction> CreateTransferAsync(
        Guid userId,
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        DateTime occurredOnUtc,
        string? note,
        Guid? savingsGoalId,
        CancellationToken cancellationToken)
    {
        EnsureUtc(occurredOnUtc);

        var transferGroupId = Guid.NewGuid();

        var transferOut = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = sourceAccountId,
            Amount = amount,
            Type = TransactionType.TransferOut,
            Note = note,
            OccurredOnUtc = occurredOnUtc,
            TransferGroupId = transferGroupId,
            SavingsGoalId = savingsGoalId
        };

        var transferIn = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = destinationAccountId,
            Amount = amount,
            Type = TransactionType.TransferIn,
            Note = note,
            OccurredOnUtc = occurredOnUtc,
            TransferGroupId = transferGroupId
        };

        _dbContext.Transactions.AddRange(transferOut, transferIn);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return transferOut;
    }

    public async Task<Transaction> ReplaceAsync(
        Transaction existing,
        Transaction replacement,
        CancellationToken cancellationToken,
        IReadOnlyList<TransactionSplitLine>? splitLines = null)
    {
        EnsureUtc(replacement.OccurredOnUtc);

        await RemoveExistingAsync(existing, cancellationToken);
        var splitTransactions = BuildSplitTransactions(replacement, splitLines);
        _dbContext.Transactions.Add(replacement);
        if (splitTransactions.Count > 0)
        {
            _dbContext.Transactions.AddRange(splitTransactions);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return replacement;
    }

    public async Task<Transaction> ReplaceWithTransferAsync(
        Transaction existing,
        Guid destinationAccountId,
        decimal amount,
        DateTime occurredOnUtc,
        string? note,
        Guid? savingsGoalId,
        CancellationToken cancellationToken)
    {
        EnsureUtc(occurredOnUtc);

        await RemoveExistingAsync(existing, cancellationToken);

        var transferGroupId = Guid.NewGuid();
        var transferOut = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = existing.UserId,
            AccountId = existing.AccountId,
            Amount = amount,
            Type = TransactionType.TransferOut,
            Note = note,
            OccurredOnUtc = occurredOnUtc,
            TransferGroupId = transferGroupId,
            SavingsGoalId = savingsGoalId
        };

        var transferIn = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = existing.UserId,
            AccountId = destinationAccountId,
            Amount = amount,
            Type = TransactionType.TransferIn,
            Note = note,
            OccurredOnUtc = occurredOnUtc,
            TransferGroupId = transferGroupId
        };

        _dbContext.Transactions.AddRange(transferOut, transferIn);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return transferOut;
    }

    private async Task RemoveExistingAsync(Transaction existing, CancellationToken cancellationToken)
    {
        if (existing.TransferGroupId.HasValue)
        {
            var linkedTransactions = await _dbContext.Transactions
                .Where(item => item.UserId == existing.UserId && item.TransferGroupId == existing.TransferGroupId.Value)
                .ToListAsync(cancellationToken);

            _dbContext.Transactions.RemoveRange(linkedTransactions);
            return;
        }

        if (existing.SplitGroupId.HasValue)
        {
            var linkedTransactions = await _dbContext.Transactions
                .Where(item => item.UserId == existing.UserId && item.SplitGroupId == existing.SplitGroupId.Value)
                .ToListAsync(cancellationToken);

            _dbContext.Transactions.RemoveRange(linkedTransactions);
            return;
        }

        _dbContext.Transactions.Remove(existing);
    }

    private static IReadOnlyList<Transaction> BuildSplitTransactions(
        Transaction parent,
        IReadOnlyList<TransactionSplitLine>? splitLines)
    {
        if (splitLines is not { Count: > 0 })
        {
            return [];
        }

        var splitGroupId = Guid.NewGuid();
        parent.SplitGroupId = splitGroupId;

        return splitLines
            .Select(line => new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = parent.UserId,
                AccountId = parent.AccountId,
                CategoryId = line.CategoryId,
                Amount = line.Amount,
                Type = parent.Type,
                Note = parent.Note,
                OccurredOnUtc = parent.OccurredOnUtc,
                SplitGroupId = splitGroupId,
                ParentTransactionId = parent.Id
            })
            .ToList();
    }

    private static void EnsureUtc(DateTime occurredOnUtc)
    {
        if (occurredOnUtc.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("OccurredOnUtc must be a UTC date/time.");
        }
    }
}
