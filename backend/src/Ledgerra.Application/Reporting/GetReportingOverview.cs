using Ledgerra.Domain.Accounts;
using Ledgerra.Domain.Reporting;
using Ledgerra.Domain.Transactions;
using Ledgerra.Domain.ExchangeRates;

namespace Ledgerra.Application.Reporting;

public sealed record GetReportingOverviewQuery(Guid UserId, string? RangePreset, Guid? AccountId, DateOnly? EndMonth);

public sealed record ReportingOverviewResult(
    string RangePreset,
    string StartMonth,
    string EndMonth,
    string CurrencyCode,
    ReportingSummaryResult Summary,
    IReadOnlyList<MonthlySpendingPoint> MonthlySpendingTrend,
    IReadOnlyList<MonthlyCashFlowPoint> IncomeVsExpense,
    IReadOnlyList<CategoryBreakdownPoint> CategoryBreakdown,
    IReadOnlyList<NetWorthPoint> NetWorthHistory,
    IReadOnlyList<ReportingWarningResult> Warnings);

public sealed record ReportingSummaryResult(
    decimal IncomeTotal,
    decimal ExpenseTotal,
    decimal NetCashFlow,
    decimal SpendingDeltaAmount,
    decimal? SpendingDeltaPercent,
    decimal NetWorthDelta);

public sealed record ReportingWarningResult(string Code, string Message);

public interface IReportingDataProvider
{
    Task<IReadOnlyList<Account>> GetAccountsAsync(Guid userId, Guid? accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        Guid userId,
        DateTime startUtc,
        DateTime endExclusiveUtc,
        Guid? accountId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetCategoryNamesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> categoryIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MonthlyAccountBalanceSnapshot>> GetSnapshotsAsync(
        Guid userId,
        DateOnly startMonthEnd,
        DateOnly endMonthEnd,
        Guid? accountId,
        CancellationToken cancellationToken);

    Task<string> GetPreferredCurrencyCodeAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FxConversionRate>> GetExchangeRatesAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IMonthlyAccountBalanceSnapshotService
{
    Task EnsureSnapshotsAsync(
        Guid userId,
        DateOnly startMonth,
        DateOnly endMonth,
        Guid? accountId,
        CancellationToken cancellationToken);

    Task RefreshFromAsync(Guid userId, DateOnly fromMonth, Guid? accountId, CancellationToken cancellationToken);
}

public sealed class GetReportingOverviewQueryHandler
{
    private static readonly HashSet<string> SupportedRangePresets = new(StringComparer.OrdinalIgnoreCase)
    {
        "3M",
        "6M",
        "12M",
        "YTD"
    };

    private readonly IReportingDataProvider _dataProvider;
    private readonly IMonthlyAccountBalanceSnapshotService _snapshotService;

    public GetReportingOverviewQueryHandler(
        IReportingDataProvider dataProvider,
        IMonthlyAccountBalanceSnapshotService snapshotService)
    {
        _dataProvider = dataProvider;
        _snapshotService = snapshotService;
    }

    public async Task<ReportingOverviewResult> HandleAsync(GetReportingOverviewQuery query, CancellationToken cancellationToken)
    {
        var rangePreset = NormalizeRangePreset(query.RangePreset);
        var endMonth = query.EndMonth ?? new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var startMonth = CalculateStartMonth(rangePreset, endMonth);
        var buckets = ReportingCalendar.BuildMonthBuckets(startMonth, endMonth);
        var startUtc = new DateTime(startMonth.Year, startMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endExclusiveUtc = new DateTime(endMonth.Year, endMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

        var transactions = await _dataProvider.GetTransactionsAsync(
            query.UserId,
            startUtc,
            endExclusiveUtc,
            query.AccountId,
            cancellationToken);

        var categoryIds = transactions
            .Where(item => TransactionSemantics.IsSpending(item) && item.CategoryId.HasValue)
            .Select(item => item.CategoryId!.Value)
            .Distinct()
            .ToArray();

        var categoryNames = await _dataProvider.GetCategoryNamesAsync(query.UserId, categoryIds, cancellationToken);
        var warnings = new List<ReportingWarningResult>();
        var currencyCode = await _dataProvider.GetPreferredCurrencyCodeAsync(query.UserId, cancellationToken);
        var rates = await _dataProvider.GetExchangeRatesAsync(query.UserId, cancellationToken);
        var conversion = ConvertTransactions(transactions, currencyCode, rates);
        warnings.AddRange(MapWarnings(conversion.Warnings));
        var monthlySpending = TransactionAggregationCalculator.BuildMonthlySpending(buckets, conversion.Transactions);
        var cashFlow = TransactionAggregationCalculator.BuildMonthlyCashFlow(buckets, conversion.Transactions);
        var categoryBreakdown = CategoryBreakdownCalculator.BuildRankedBreakdown(conversion.Transactions, categoryNames);
        var accounts = await _dataProvider.GetAccountsAsync(query.UserId, query.AccountId, cancellationToken);
        IReadOnlyList<NetWorthPoint> netWorthHistory = [];

        if (accounts.Count > 0)
        {
            await _snapshotService.EnsureSnapshotsAsync(query.UserId, startMonth, endMonth, query.AccountId, cancellationToken);
            var snapshots = await _dataProvider.GetSnapshotsAsync(
                query.UserId,
                ReportingCalendar.MonthEnd(startMonth),
                ReportingCalendar.MonthEnd(endMonth),
                query.AccountId,
                cancellationToken);
            var netWorthConversion = ConvertSnapshots(snapshots, currencyCode, rates);
            var availableBuckets = buckets
                .Where(bucket => !netWorthConversion.UnavailableMonths.Contains(ReportingCalendar.MonthEnd(bucket.MonthStart)))
                .ToList();

            if (availableBuckets.Count == 0 && netWorthConversion.UnavailableMonths.Count > 0)
            {
                warnings.Add(new ReportingWarningResult(
                    "MixedCurrencyNetWorthExcluded",
                    $"Net worth history was excluded because one or more account balances could not be converted to {currencyCode}."));
            }
            else
            {
                warnings.AddRange(MapWarnings(netWorthConversion.Warnings.Where(warning => warning.Code != "MissingFxRate")));
                netWorthHistory = NetWorthRollupCalculator.BuildNetWorthHistory(availableBuckets, netWorthConversion.Snapshots, currencyCode);
            }
        }

        return new ReportingOverviewResult(
            rangePreset,
            ReportingCalendar.FormatMonth(startMonth),
            ReportingCalendar.FormatMonth(endMonth),
            currencyCode,
            BuildSummary(cashFlow, monthlySpending, netWorthHistory),
            monthlySpending,
            cashFlow,
            categoryBreakdown,
            netWorthHistory,
            warnings);
    }

    private sealed record ConvertedTransactions(IReadOnlyList<Transaction> Transactions, IReadOnlyList<FxConversionWarning> Warnings);

    private sealed record ConvertedSnapshots(
        IReadOnlyList<MonthlyAccountBalanceSnapshot> Snapshots,
        IReadOnlyList<FxConversionWarning> Warnings,
        IReadOnlySet<DateOnly> UnavailableMonths);

    private static ConvertedTransactions ConvertTransactions(
        IReadOnlyList<Transaction> transactions,
        string currencyCode,
        IReadOnlyList<FxConversionRate> rates)
    {
        var warnings = new List<FxConversionWarning>();
        var converted = transactions.Select(transaction =>
        {
            var month = new DateOnly(transaction.OccurredOnUtc.Year, transaction.OccurredOnUtc.Month, 1);
            var sourceCurrency = transaction.Account?.CurrencyCode ?? currencyCode;
            var result = FxRateConverter.Convert(transaction.Amount, sourceCurrency, currencyCode, month, rates);
            warnings.AddRange(result.Warnings);

            return new Transaction
            {
                Id = transaction.Id,
                UserId = transaction.UserId,
                AccountId = transaction.AccountId,
                CategoryId = transaction.CategoryId,
                Amount = result.Amount,
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

        return new ConvertedTransactions(converted, warnings);
    }

    private static ConvertedSnapshots ConvertSnapshots(
        IReadOnlyList<MonthlyAccountBalanceSnapshot> snapshots,
        string currencyCode,
        IReadOnlyList<FxConversionRate> rates)
    {
        var warnings = new List<FxConversionWarning>();
        var unavailableMonths = new HashSet<DateOnly>();
        var converted = new List<MonthlyAccountBalanceSnapshot>();

        foreach (var snapshot in snapshots)
        {
            var month = new DateOnly(snapshot.MonthEndDate.Year, snapshot.MonthEndDate.Month, 1);
            var result = FxRateConverter.Convert(snapshot.Balance, snapshot.CurrencyCode, currencyCode, month, rates);
            warnings.AddRange(result.Warnings);
            if (result.Warnings.Any(warning => warning.Code == "MissingFxRate"))
            {
                unavailableMonths.Add(snapshot.MonthEndDate);
            }

            converted.Add(new MonthlyAccountBalanceSnapshot
            {
                Id = snapshot.Id,
                UserId = snapshot.UserId,
                AccountId = snapshot.AccountId,
                MonthEndDate = snapshot.MonthEndDate,
                Balance = result.Amount,
                CurrencyCode = currencyCode,
                Account = snapshot.Account
            });
        }

        converted.RemoveAll(snapshot => unavailableMonths.Contains(snapshot.MonthEndDate));

        return new ConvertedSnapshots(converted, warnings, unavailableMonths);
    }

    private static IEnumerable<ReportingWarningResult> MapWarnings(IEnumerable<FxConversionWarning> warnings)
    {
        return warnings
            .GroupBy(warning => new { warning.Code, warning.Message })
            .Select(group => new ReportingWarningResult(group.Key.Code, group.Key.Message));
    }

    private static ReportingSummaryResult BuildSummary(
        IReadOnlyList<MonthlyCashFlowPoint> cashFlow,
        IReadOnlyList<MonthlySpendingPoint> spending,
        IReadOnlyList<NetWorthPoint> netWorthHistory)
    {
        var incomeTotal = cashFlow.Sum(item => item.Income);
        var expenseTotal = cashFlow.Sum(item => item.Expenses);
        var currentSpending = spending.Count > 0 ? spending[^1].Amount : 0m;
        var previousSpending = spending.Count > 1 ? spending[^2].Amount : 0m;
        var spendingDeltaAmount = currentSpending - previousSpending;
        decimal? spendingDeltaPercent = previousSpending == 0m
            ? null
            : Math.Round(spendingDeltaAmount / previousSpending * 100m, 2);
        var netWorthDelta = netWorthHistory.Count > 1
            ? netWorthHistory[^1].NetWorth - netWorthHistory[0].NetWorth
            : 0m;

        return new ReportingSummaryResult(
            incomeTotal,
            expenseTotal,
            incomeTotal - expenseTotal,
            spendingDeltaAmount,
            spendingDeltaPercent,
            netWorthDelta);
    }

    private static string NormalizeRangePreset(string? rangePreset)
    {
        if (string.IsNullOrWhiteSpace(rangePreset))
        {
            return "12M";
        }

        return SupportedRangePresets.Contains(rangePreset) ? rangePreset.ToUpperInvariant() : "12M";
    }

    private static DateOnly CalculateStartMonth(string rangePreset, DateOnly endMonth)
    {
        var normalizedEnd = new DateOnly(endMonth.Year, endMonth.Month, 1);

        return rangePreset switch
        {
            "3M" => normalizedEnd.AddMonths(-2),
            "6M" => normalizedEnd.AddMonths(-5),
            "YTD" => new DateOnly(normalizedEnd.Year, 1, 1),
            _ => normalizedEnd.AddMonths(-11)
        };
    }
}
