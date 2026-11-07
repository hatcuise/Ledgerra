using Ledgerra.Domain.Accounts;
using Ledgerra.Domain.Budgets;
using Ledgerra.Domain.Reporting;
using Ledgerra.Domain.Transactions;
using Ledgerra.Domain.ExchangeRates;

namespace Ledgerra.Application.Dashboard;

public sealed record GetDashboardSummaryQuery(Guid UserId, int Year, int Month);

public sealed record DashboardSummaryResult(
    decimal Income,
    decimal Expenses,
    decimal Net,
    decimal BudgetRemaining,
    IReadOnlyList<DashboardCategorySpendResult> TopCategories,
    IReadOnlyList<AccountBalanceSnapshotResult> Accounts,
    DashboardTrendsResult Trends,
    string CurrencyCode,
    IReadOnlyList<DashboardWarningResult> Warnings);

public sealed record DashboardWarningResult(string Code, string Message);

public sealed record DashboardCategorySpendResult(Guid CategoryId, string CategoryName, decimal Amount);

public sealed record AccountBalanceSnapshotResult(Guid AccountId, string Name, decimal Balance);

public sealed record DashboardTrendsResult(
    decimal SpendingDeltaAmount,
    decimal? SpendingDeltaPercent,
    IReadOnlyList<DashboardSpendingSparklinePointResult> SpendingSparkline);

public sealed record DashboardSpendingSparklinePointResult(string Month, decimal Amount);

public interface IDashboardSummaryDataProvider
{
    Task<IReadOnlyList<Transaction>> GetTransactionsForMonthAsync(Guid userId, int year, int month, CancellationToken cancellationToken);

    Task<IReadOnlyList<Transaction>> GetTransactionsForRangeAsync(
        Guid userId,
        DateTime startUtc,
        DateTime endExclusiveUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Account>> GetAccountsAsync(Guid userId, CancellationToken cancellationToken);

    Task<BudgetPeriod?> GetBudgetPeriodAsync(Guid userId, int year, int month, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, decimal>> GetBudgetCarryForwardAsync(Guid userId, int year, int month, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetCategoryNamesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> categoryIds,
        CancellationToken cancellationToken);

    Task<string> GetPreferredCurrencyCodeAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FxConversionRate>> GetExchangeRatesAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class GetDashboardSummaryQueryHandler
{
    private readonly IDashboardSummaryDataProvider _dataProvider;

    public GetDashboardSummaryQueryHandler(IDashboardSummaryDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

    public async Task<DashboardSummaryResult> HandleAsync(GetDashboardSummaryQuery query, CancellationToken cancellationToken)
    {
        var currencyCode = await _dataProvider.GetPreferredCurrencyCodeAsync(query.UserId, cancellationToken);
        var rates = await _dataProvider.GetExchangeRatesAsync(query.UserId, cancellationToken);
        var warnings = new List<FxConversionWarning>();
        var transactions = ConvertTransactions(
            await _dataProvider.GetTransactionsForMonthAsync(query.UserId, query.Year, query.Month, cancellationToken),
            currencyCode,
            rates,
            warnings);
        var trendStartMonth = new DateOnly(query.Year, query.Month, 1).AddMonths(-5);
        var trendStartUtc = new DateTime(trendStartMonth.Year, trendStartMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var trendEndUtc = new DateTime(query.Year, query.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        var trendTransactions = ConvertTransactions(
            await _dataProvider.GetTransactionsForRangeAsync(query.UserId, trendStartUtc, trendEndUtc, cancellationToken),
            currencyCode,
            rates,
            warnings);
        var accounts = await _dataProvider.GetAccountsAsync(query.UserId, cancellationToken);
        var period = await _dataProvider.GetBudgetPeriodAsync(query.UserId, query.Year, query.Month, cancellationToken);

        var budgetCarryForward = period is null
            ? new Dictionary<Guid, decimal>()
            : await _dataProvider.GetBudgetCarryForwardAsync(query.UserId, query.Year, query.Month, cancellationToken);
        var budgetRemaining = period is null
            ? 0m
            : BudgetSummaryCalculator.BuildMonthlySummary(period, transactions, budgetCarryForward).TotalRemaining;

        var topCategoryIds = transactions
            .Where(IsCategoryExpense)
            .Select(item => item.CategoryId!.Value)
            .Distinct()
            .ToArray();

        var categoryNames = await _dataProvider.GetCategoryNamesAsync(query.UserId, topCategoryIds, cancellationToken);

        var topCategories = transactions
            .Where(IsCategoryExpense)
            .GroupBy(item => item.CategoryId!.Value)
            .Select(group => new DashboardCategorySpendResult(
                group.Key,
                categoryNames.GetValueOrDefault(group.Key, "Uncategorized"),
                group.Sum(TransactionSemantics.SpendingAmount)))
            .OrderByDescending(item => item.Amount)
            .Take(5)
            .ToList();

        var income = transactions.Where(item => item.Type == TransactionType.Income && !item.ParentTransactionId.HasValue).Sum(item => item.Amount);
        var expenses = transactions.Where(item => TransactionSemantics.IsSpending(item) && !item.ParentTransactionId.HasValue).Sum(TransactionSemantics.SpendingAmount);

        return new DashboardSummaryResult(
            income,
            expenses,
            income - expenses,
            budgetRemaining,
            topCategories,
            accounts.Select(account => new AccountBalanceSnapshotResult(
                account.Id,
                account.Name,
                ConvertAmount(
                    AccountBalanceCalculator.Calculate(account, account.Transactions),
                    account.CurrencyCode,
                    currencyCode,
                    new DateOnly(query.Year, query.Month, 1),
                    rates,
                    warnings))).ToList(),
            BuildTrends(query.Year, query.Month, trendTransactions),
            currencyCode,
            MapWarnings(warnings).ToList());
    }

    private static IReadOnlyList<Transaction> ConvertTransactions(
        IReadOnlyList<Transaction> transactions,
        string currencyCode,
        IReadOnlyList<FxConversionRate> rates,
        List<FxConversionWarning> warnings)
    {
        return transactions.Select(transaction =>
        {
            var month = new DateOnly(transaction.OccurredOnUtc.Year, transaction.OccurredOnUtc.Month, 1);
            var amount = ConvertAmount(transaction.Amount, transaction.Account?.CurrencyCode ?? currencyCode, currencyCode, month, rates, warnings);
            return new Transaction
            {
                Id = transaction.Id,
                UserId = transaction.UserId,
                AccountId = transaction.AccountId,
                CategoryId = transaction.CategoryId,
                Amount = amount,
                Type = transaction.Type,
                Note = transaction.Note,
                OccurredOnUtc = transaction.OccurredOnUtc,
                TransferGroupId = transaction.TransferGroupId,
                SplitGroupId = transaction.SplitGroupId,
                ParentTransactionId = transaction.ParentTransactionId,
                SavingsGoalId = transaction.SavingsGoalId,
                Account = transaction.Account,
                Category = transaction.Category
            };
        }).ToList();
    }

    private static decimal ConvertAmount(
        decimal amount,
        string fromCurrencyCode,
        string toCurrencyCode,
        DateOnly month,
        IReadOnlyList<FxConversionRate> rates,
        List<FxConversionWarning> warnings)
    {
        var result = FxRateConverter.Convert(amount, fromCurrencyCode, toCurrencyCode, month, rates);
        warnings.AddRange(result.Warnings);
        return result.Amount;
    }

    private static IEnumerable<DashboardWarningResult> MapWarnings(IEnumerable<FxConversionWarning> warnings)
    {
        return warnings
            .GroupBy(warning => new { warning.Code, warning.Message })
            .Select(group => new DashboardWarningResult(group.Key.Code, group.Key.Message));
    }

    private static DashboardTrendsResult BuildTrends(int year, int month, IReadOnlyList<Transaction> transactions)
    {
        var endMonth = new DateOnly(year, month, 1);
        var buckets = ReportingCalendar.BuildMonthBuckets(endMonth.AddMonths(-5), endMonth);
        var spending = TransactionAggregationCalculator.BuildMonthlySpending(buckets, transactions)
            .Select(item => new DashboardSpendingSparklinePointResult(item.Month, item.Amount))
            .ToList();
        var currentSpending = spending.Count > 0 ? spending[^1].Amount : 0m;
        var previousSpending = spending.Count > 1 ? spending[^2].Amount : 0m;
        var delta = currentSpending - previousSpending;
        decimal? percent = previousSpending == 0m ? null : Math.Round(delta / previousSpending * 100m, 2);

        return new DashboardTrendsResult(delta, percent, spending);
    }

    private static bool IsCategoryExpense(Transaction transaction)
    {
        return TransactionSemantics.IsSpending(transaction) &&
            transaction.CategoryId.HasValue &&
            (transaction.ParentTransactionId.HasValue || !transaction.SplitGroupId.HasValue);
    }
}
