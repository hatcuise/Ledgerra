using Ledgerra.Application.Accounts;
using Ledgerra.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public sealed class AccountStore : IAccountStore
{
    private readonly LedgerraDbContext _dbContext;

    public AccountStore(LedgerraDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<Account>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Accounts
            .Where(account => account.UserId == userId)
            .Include(account => account.Transactions)
            .OrderBy(account => account.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Account?> GetByIdAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        return await _dbContext.Accounts
            .Where(item => item.UserId == userId && item.Id == accountId)
            .Include(item => item.Transactions)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Account> CreateAsync(Account account, CancellationToken cancellationToken)
    {
        _dbContext.Accounts.Add(account);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task<Account?> UpdateAsync(
        Guid userId,
        Guid accountId,
        string name,
        AccountType type,
        string currencyCode,
        decimal openingBalance,
        bool isActive,
        bool? excludeFromBudget,
        bool? excludeFromNetWorth,
        string? institutionName,
        string? accountNumberMasked,
        AccountIconKind? iconKind,
        CancellationToken cancellationToken)
    {
        var account = await _dbContext.Accounts.SingleOrDefaultAsync(item => item.UserId == userId && item.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        account.Name = name;
        account.Type = type;
        account.CurrencyCode = currencyCode;
        account.OpeningBalance = openingBalance;
        account.IsActive = isActive;
        if (excludeFromBudget.HasValue)
        {
            account.ExcludeFromBudget = excludeFromBudget.Value;
        }
        if (excludeFromNetWorth.HasValue)
        {
            account.ExcludeFromNetWorth = excludeFromNetWorth.Value;
        }
        account.InstitutionName = institutionName;
        account.AccountNumberMasked = accountNumberMasked;
        if (iconKind.HasValue)
        {
            account.IconKind = iconKind.Value;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await _dbContext.Accounts
            .Include(item => item.Transactions)
            .SingleAsync(item => item.UserId == userId && item.Id == accountId, cancellationToken);
    }

    public async Task<AccountDeleteStatus> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await _dbContext.Accounts
            .Include(item => item.Transactions)
            .SingleOrDefaultAsync(item => item.UserId == userId && item.Id == accountId, cancellationToken);

        if (account is null)
        {
            return AccountDeleteStatus.NotFound;
        }

        if (account.Transactions.Count > 0)
        {
            return AccountDeleteStatus.HasTransactions;
        }

        foreach (var mapping in await _dbContext.ConnectedBankAccounts.Where(x => x.UserId == userId && x.LedgerAccountId == accountId).ToListAsync(cancellationToken)) mapping.LedgerAccountId = null;
        _dbContext.Accounts.Remove(account);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return AccountDeleteStatus.Deleted;
    }
}
