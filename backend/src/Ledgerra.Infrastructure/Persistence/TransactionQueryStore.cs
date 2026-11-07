using Ledgerra.Application.Transactions;
using Ledgerra.Domain.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public sealed class TransactionQueryStore : ITransactionQueryStore
{
    private readonly LedgerraDbContext _dbContext;

    public TransactionQueryStore(LedgerraDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<Transaction>> GetAllAsync(
        Guid userId,
        IReadOnlyCollection<Guid> accountIds,
        IReadOnlyCollection<Guid> categoryIds,
        TransactionType? type,
        DateTime? fromUtc,
        DateTime? toUtc,
        decimal? minAmount,
        decimal? maxAmount,
        string? search,
        bool uncategorizedOnly,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.Transactions.Where(item => item.UserId == userId);

        if (accountIds.Count > 0)
        {
            query = query.Where(item => accountIds.Contains(item.AccountId));
        }

        if (categoryIds.Count > 0)
        {
            query = query.Where(item => item.CategoryId.HasValue && categoryIds.Contains(item.CategoryId.Value));
        }

        if (type.HasValue)
        {
            query = query.Where(item => item.Type == type.Value);
        }

        if (fromUtc.HasValue)
        {
            query = query.Where(item => item.OccurredOnUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(item => item.OccurredOnUtc <= toUtc.Value);
        }

        if (minAmount.HasValue)
        {
            query = query.Where(item => item.Amount >= minAmount.Value);
        }

        if (maxAmount.HasValue)
        {
            query = query.Where(item => item.Amount <= maxAmount.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.Trim().ToLower();
            query = query.Where(item => item.Note != null && item.Note.ToLower().Contains(normalizedSearch));
        }

        if (uncategorizedOnly)
        {
            query = query.Where(item => (item.Type == TransactionType.Expense || item.Type == TransactionType.Refund) && item.CategoryId == null);
        }

        return await query
            .OrderByDescending(item => item.OccurredOnUtc)
            .ToListAsync(cancellationToken);
    }

    public Task<Transaction?> GetByIdAsync(Guid userId, Guid transactionId, CancellationToken cancellationToken)
    {
        return _dbContext.Transactions.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == transactionId,
            cancellationToken);
    }
}
