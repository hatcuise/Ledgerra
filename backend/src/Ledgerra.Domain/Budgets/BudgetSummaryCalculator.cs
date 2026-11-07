using Ledgerra.Domain.Transactions;

namespace Ledgerra.Domain.Budgets;

public static class BudgetSummaryCalculator
{
    public static BudgetSummary BuildMonthlySummary(BudgetPeriod period, IEnumerable<Transaction> transactions, IReadOnlyDictionary<Guid, decimal>? carryForwardByCategory = null)
    {
        var categorySummaries = period.CategoryLimits
            .Select(limit =>
            {
                var spent = transactions
                    .Where(transaction =>
                        TransactionSemantics.IsSpending(transaction) &&
                        transaction.CategoryId == limit.CategoryId &&
                        (transaction.ParentTransactionId.HasValue || !transaction.SplitGroupId.HasValue) &&
                        transaction.OccurredOnUtc.Year == period.Year &&
                        transaction.OccurredOnUtc.Month == period.Month)
                    .Sum(TransactionSemantics.SpendingAmount);

                var carryForward = 0m;
                if (limit.CarryOverUnspent && carryForwardByCategory is not null)
                {
                    carryForwardByCategory.TryGetValue(limit.CategoryId, out carryForward);
                }

                var available = limit.PlannedAmount + carryForward;

                return new BudgetCategorySummary
                {
                    CategoryId = limit.CategoryId,
                    CategoryName = limit.Category?.Name ?? "Uncategorized",
                    Planned = limit.PlannedAmount,
                    CarryForward = carryForward,
                    Available = available,
                    CarryOverUnspent = limit.CarryOverUnspent,
                    Spent = spent,
                    Remaining = available - spent
                };
            })
            .ToList();

        var totalAvailable = categorySummaries.Sum(item => item.Available);
        var totalSpent = categorySummaries.Sum(item => item.Spent);

        return new BudgetSummary
        {
            TotalPlanned = totalAvailable,
            TotalSpent = totalSpent,
            TotalRemaining = totalAvailable - totalSpent,
            Categories = categorySummaries
        };
    }
}
