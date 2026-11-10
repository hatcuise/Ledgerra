using Ledgerra.Domain.Budgets;
using Ledgerra.Domain.ExchangeRates;
using Ledgerra.Domain.Insights;
using Ledgerra.Domain.Transactions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ledgerra.Api.Services.Insights;

public sealed class SpendingInsightsService(LedgerraDbContext db)
{
    public async Task<SpendingInsights> BuildAsync(Guid userId, DateOnly month, DateOnly today, CancellationToken ct, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        var preferences = await db.InsightPreferences.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct) ?? new InsightPreferences { UserId = userId };
        var currency = await db.Users.Where(x => x.Id == userId).Select(x => x.PreferredCurrencyCode).SingleAsync(ct);
        var periods = await db.BudgetPeriods.AsNoTracking().Include(x => x.CategoryLimits).ThenInclude(x => x.Category)
            .Where(x => x.UserId == userId && (x.Year < month.Year || (x.Year == month.Year && x.Month <= month.Month)))
            .OrderBy(x => x.Year).ThenBy(x => x.Month).ToListAsync(ct);
        var periodByMonth = periods.ToDictionary(x => new DateOnly(x.Year, x.Month, 1));
        var carryStart = month;
        while (periodByMonth.TryGetValue(carryStart, out var period) && period.CategoryLimits.Any(x => x.CarryOverUnspent) && carryStart.Year > 1)
        { var previous = carryStart.AddMonths(-1); if (!periodByMonth.ContainsKey(previous)) break; carryStart = previous; }
        var historyStart = month.AddMonths(-3);
        var start = UserCalendar.UtcStart(carryStart < historyStart ? carryStart : historyStart, zone);
        var last = month.AddMonths(1).AddDays(-1); if (today < last) last = today;
        var end = UserCalendar.UtcStart(last.AddDays(1), zone);
        var rows = await db.Transactions.AsNoTracking().Include(x => x.Account)
            .Where(x => x.UserId == userId && x.OccurredOnUtc >= start && x.OccurredOnUtc < end && !x.Account!.ExcludeFromBudget
                && (x.Type == TransactionType.Expense || x.Type == TransactionType.Refund || x.Type == TransactionType.Income)).ToListAsync(ct);
        var rates = await db.ExchangeRates.AsNoTracking().Where(x => x.UserId == userId).Select(x => new FxConversionRate(x.FromCurrencyCode, x.ToCurrencyCode, x.Month, x.Rate)).ToListAsync(ct);
        var warnings = new HashSet<string>(); var missing = new List<Transaction>(); var converted = new List<Transaction>();
        foreach (var row in rows)
        {
            // Work on detached projections: budget and insight grouping use the user's calendar.
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.OccurredOnUtc, DateTimeKind.Utc), zone));
            row.OccurredOnUtc = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var result = FxRateConverter.Convert(row.Amount, row.Account!.CurrencyCode, currency, new DateOnly(row.OccurredOnUtc.Year, row.OccurredOnUtc.Month, 1), rates);
            foreach (var warning in result.Warnings) warnings.Add(warning.Message);
            if (result.Warnings.Any(x => x.Code == "MissingFxRate")) { missing.Add(row); continue; }
            row.Amount = result.Amount; converted.Add(row);
        }
        var convertedByMonth = converted.ToLookup(x => new DateOnly(x.OccurredOnUtc.Year, x.OccurredOnUtc.Month, 1));
        var carry = new Dictionary<Guid, decimal>(); DateOnly? previousPeriod = null; BudgetSummary? selectedBudget = null;
        foreach (var period in periods.Where(x => new DateOnly(x.Year, x.Month, 1) >= carryStart))
        {
            var date = new DateOnly(period.Year, period.Month, 1);
            if (previousPeriod?.AddMonths(1) != date) carry.Clear();
            var summary = BudgetSummaryCalculator.BuildMonthlySummary(period, convertedByMonth[date], carry);
            if (date == month) selectedBudget = summary;
            carry = summary.Categories.Where(x => x.CarryOverUnspent && x.Remaining > 0).ToDictionary(x => x.CategoryId, x => x.Remaining);
            previousPeriod = date;
        }
        var names = await db.Categories.AsNoTracking().Where(x => x.UserId == userId).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var payees = await db.Payees.AsNoTracking().Where(x => x.UserId == userId).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var ids = converted.Where(x => x.ParentTransactionId == null).Select(x => x.Id).ToArray();
        var payeeByEntry = await db.EntryMetadata.AsNoTracking().Where(x => x.UserId == userId && ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.PayeeId, ct);
        var entries = converted.Where(x => DateOnly.FromDateTime(x.OccurredOnUtc) >= historyStart)
            .Select(x => new InsightEntry(x.Id, x.CategoryId, x.Amount, x.Type.ToString(), DateOnly.FromDateTime(x.OccurredOnUtc), x.ParentTransactionId, x.SplitGroupId, payeeByEntry.GetValueOrDefault(x.Id), x.Note)).ToList();
        if (missing.Count > 0) warnings.Add("Some amounts could not be converted. Totals contain known amounts only; monetary alerts, comparisons and projections are paused until exchange rates are supplied.");
        var resultData = SpendingInsightsCalculator.Build(month, today, currency, entries,
            selectedBudget?.Categories.Select(x => new InsightBudget(x.CategoryId, x.CategoryName, x.Available)).ToList() ?? [], names, payees, preferences,
            missing.Count == 0, missing.Count(x => x.ParentTransactionId == null), warnings.OrderBy(x => x).ToList());
        var monthKey = $"{month:yyyy-MM}";
        var dismissed = (await db.InsightDismissals.AsNoTracking().Where(x => x.UserId == userId && x.Month == monthKey).Select(x => x.AlertKey).ToListAsync(ct)).ToHashSet();
        return resultData with { TimeZone = zone.Id, Alerts = resultData.Alerts.Select(x => x with { Dismissed = dismissed.Contains(x.Key) }).ToList() };
    }
}
