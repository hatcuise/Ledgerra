using System.Globalization;
using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/backup")]
public sealed class BackupController : ControllerBase
{
    private readonly LedgerraDbContext _dbContext;

    public BackupController(LedgerraDbContext dbContext) => _dbContext = dbContext;

    [HttpGet("export")]
    public async Task<ActionResult<BackupArchiveResponse>> Export(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();

        var accounts = await _dbContext.Accounts.Where(x => x.UserId == userId).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var categories = await _dbContext.Categories.Where(x => x.UserId == userId).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var transactions = await _dbContext.Transactions.Where(x => x.UserId == userId).OrderBy(x => x.OccurredOnUtc).ToListAsync(cancellationToken);
        var budgetPeriods = await _dbContext.BudgetPeriods
            .Where(x => x.UserId == userId)
            .Include(x => x.CategoryLimits)
            .OrderBy(x => x.Year)
            .ThenBy(x => x.Month)
            .ToListAsync(cancellationToken);

        var savingsGoals = await _dbContext.SavingsGoals.Where(x => x.UserId == userId).OrderBy(x => x.CreatedAtUtc).ToListAsync(cancellationToken);

        return Ok(new BackupArchiveResponse(
            7,
            DateTimeOffset.UtcNow.ToString("O"),
            accounts.Select(x => new BackupAccountResponse(x.Id, x.Name, x.Type.ToString(), x.CurrencyCode, x.OpeningBalance, x.IsActive, x.InstitutionName, x.AccountNumberMasked, x.IconKind.ToString(), x.ExcludeFromBudget, x.ExcludeFromNetWorth)).ToList(),
            categories.Select(x => new BackupCategoryResponse(x.Id, x.Name, x.Kind.ToString(), x.Color, x.ParentCategoryId)).ToList(),
            transactions.Select(x => new BackupTransactionResponse(x.Id, x.AccountId, x.CategoryId, x.Amount, x.Type.ToString(), x.OccurredOnUtc.ToString("O"), x.Note, x.TransferGroupId, x.SplitGroupId, x.ParentTransactionId, x.SavingsGoalId)).ToList(),
            budgetPeriods.Select(x => new BackupBudgetPeriodResponse(
                x.Id,
                x.Year,
                x.Month,
                x.CategoryLimits.Select(limit => new BackupBudgetCategoryLimitResponse(limit.Id, limit.CategoryId, limit.PlannedAmount, limit.CarryOverUnspent)).ToList())).ToList(),
            savingsGoals.Select(x => new BackupSavingsGoalResponse(x.Id, x.Name, x.TargetAmount, x.DeadlineUtc?.ToString("O"), x.CreatedAtUtc.ToString("O"), x.UpdatedAtUtc.ToString("O"))).ToList(),
            await _dbContext.ImportProfiles.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.Payees.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.EntryMetadata.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.Reconciliations.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.RecurringTransactionTemplates.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.CategorizationRules.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.ExchangeRates.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.TransferMatches.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.SavedLedgerViews.Where(x => x.UserId == userId).ToListAsync(cancellationToken),
            await _dbContext.InsightPreferences.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken),
            await _dbContext.InsightDismissals.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken)));

    }

    [HttpPost("restore")]
    public async Task<ActionResult> Restore([FromBody] BackupArchiveResponse archive, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();

        if (archive.Version is < 2 or > 7)
        {
            return BadRequest(new ProblemDetails { Title = "Unsupported backup archive version." });
        }

        var referenceError = ValidateArchiveReferences(archive);
        if (referenceError is not null)
        {
            return BadRequest(new ProblemDetails { Title = referenceError });
        }

        var bookkeepingError = ValidateBookkeeping(archive);
        if (bookkeepingError is not null) return BadRequest(new ProblemDetails { Title = bookkeepingError });

        var shapeError = ValidateArchiveShape(archive);
        if (shapeError is not null)
        {
            return BadRequest(new ProblemDetails { Title = shapeError });
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Provider tokens are intentionally excluded from portable archives. Re-map after restore.
        foreach (var mapping in await _dbContext.ConnectedBankAccounts.Where(x => x.UserId == userId).ToListAsync(cancellationToken)) mapping.LedgerAccountId = null;
        var budgetPeriodIds = _dbContext.BudgetPeriods.Where(x => x.UserId == userId).Select(x => x.Id);
        _dbContext.BudgetCategoryLimits.RemoveRange(_dbContext.BudgetCategoryLimits.Where(x => budgetPeriodIds.Contains(x.BudgetPeriodId)));
        _dbContext.BudgetPeriods.RemoveRange(_dbContext.BudgetPeriods.Where(x => x.UserId == userId));
        _dbContext.InsightDismissals.RemoveRange(_dbContext.InsightDismissals.Where(x => x.UserId == userId));
        _dbContext.InsightPreferences.RemoveRange(_dbContext.InsightPreferences.Where(x => x.UserId == userId));
        _dbContext.SavedLedgerViews.RemoveRange(_dbContext.SavedLedgerViews.Where(x => x.UserId == userId));
        _dbContext.TransferMatches.RemoveRange(_dbContext.TransferMatches.Where(x => x.UserId == userId));
        _dbContext.EntryMetadata.RemoveRange(_dbContext.EntryMetadata.Where(x => x.UserId == userId));
        _dbContext.Reconciliations.RemoveRange(_dbContext.Reconciliations.Where(x => x.UserId == userId));
        _dbContext.Payees.RemoveRange(_dbContext.Payees.Where(x => x.UserId == userId));
        _dbContext.ImportProfiles.RemoveRange(_dbContext.ImportProfiles.Where(x => x.UserId == userId));
        _dbContext.RecurringTransactionTemplates.RemoveRange(_dbContext.RecurringTransactionTemplates.Where(x => x.UserId == userId));
        _dbContext.CategorizationRules.RemoveRange(_dbContext.CategorizationRules.Where(x => x.UserId == userId));
        _dbContext.ExchangeRates.RemoveRange(_dbContext.ExchangeRates.Where(x => x.UserId == userId));
        _dbContext.Transactions.RemoveRange(_dbContext.Transactions.Where(x => x.UserId == userId));
        _dbContext.Categories.RemoveRange(_dbContext.Categories.Where(x => x.UserId == userId));
        _dbContext.Accounts.RemoveRange(_dbContext.Accounts.Where(x => x.UserId == userId));
        _dbContext.SavingsGoals.RemoveRange(_dbContext.SavingsGoals.Where(x => x.UserId == userId));
        await _dbContext.SaveChangesAsync(cancellationToken);

        var accounts = archive.Accounts.Select(x => new Ledgerra.Domain.Accounts.Account
        {
            Id = x.Id,
            UserId = userId,
            Name = x.Name,
            Type = Enum.Parse<Ledgerra.Domain.Accounts.AccountType>(x.Type, ignoreCase: true),
            CurrencyCode = x.CurrencyCode,
            OpeningBalance = x.OpeningBalance,
            IsActive = x.IsActive,
            ExcludeFromBudget = x.ExcludeFromBudget,
            ExcludeFromNetWorth = x.ExcludeFromNetWorth,
            InstitutionName = x.InstitutionName,
            AccountNumberMasked = x.AccountNumberMasked,
            IconKind = Enum.Parse<Ledgerra.Domain.Accounts.AccountIconKind>(x.IconKind, ignoreCase: true)
        }).ToList();
        var categories = archive.Categories.Select(x => new Ledgerra.Domain.Categories.Category
        {
            Id = x.Id,
            UserId = userId,
            Name = x.Name,
            Kind = Enum.Parse<Ledgerra.Domain.Categories.CategoryKind>(x.Kind, ignoreCase: true),
            Color = x.Color, ParentCategoryId = x.ParentCategoryId
        }).ToList();
        var transactions = archive.Transactions.Select(x => new Ledgerra.Domain.Transactions.Transaction
        {
            Id = x.Id,
            UserId = userId,
            AccountId = x.AccountId,
            CategoryId = x.CategoryId,
            Amount = x.Amount,
            Type = Enum.Parse<Ledgerra.Domain.Transactions.TransactionType>(x.Type, ignoreCase: true),
            OccurredOnUtc = DateTime.Parse(x.OccurredOnUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Note = x.Note,
            TransferGroupId = x.TransferGroupId,
            SplitGroupId = x.SplitGroupId,
            ParentTransactionId = x.ParentTransactionId,
            SavingsGoalId = x.SavingsGoalId
        }).ToList();
        var savingsGoals = (archive.SavingsGoals ?? []).Select(x => new Ledgerra.Domain.Goals.SavingsGoal
        {
            Id = x.Id,
            UserId = userId,
            Name = x.Name,
            TargetAmount = x.TargetAmount,
            DeadlineUtc = string.IsNullOrWhiteSpace(x.DeadlineUtc) ? null : DateTime.Parse(x.DeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CreatedAtUtc = string.IsNullOrWhiteSpace(x.CreatedAtUtc) ? DateTime.UtcNow : DateTime.Parse(x.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAtUtc = string.IsNullOrWhiteSpace(x.UpdatedAtUtc) ? DateTime.UtcNow : DateTime.Parse(x.UpdatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        }).ToList();

        var periods = archive.BudgetPeriods.Select(x => new Ledgerra.Domain.Budgets.BudgetPeriod
        {
            Id = x.Id,
            UserId = userId,
            Year = x.Year,
            Month = x.Month
        }).ToList();
        var limits = archive.BudgetPeriods.SelectMany(x => x.CategoryLimits.Select(limit => new Ledgerra.Domain.Budgets.BudgetCategoryLimit
        {
            Id = limit.Id,
            BudgetPeriodId = x.Id,
            CategoryId = limit.CategoryId,
            PlannedAmount = limit.PlannedAmount,
            CarryOverUnspent = limit.CarryOverUnspent
        })).ToList();

        await _dbContext.Accounts.AddRangeAsync(accounts, cancellationToken);
        await _dbContext.Categories.AddRangeAsync(categories, cancellationToken);
        await _dbContext.SavingsGoals.AddRangeAsync(savingsGoals, cancellationToken);
        await _dbContext.Transactions.AddRangeAsync(transactions, cancellationToken);
        await _dbContext.BudgetPeriods.AddRangeAsync(periods, cancellationToken);
        await _dbContext.BudgetCategoryLimits.AddRangeAsync(limits, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (archive.InsightPreferences is { } insightPreferences) { insightPreferences.UserId = userId; _dbContext.InsightPreferences.Add(insightPreferences); }
        foreach (var x in archive.InsightDismissals ?? []) { x.UserId = userId; _dbContext.InsightDismissals.Add(x); }
        foreach (var x in archive.SavedLedgerViews ?? []) { x.UserId = userId; _dbContext.SavedLedgerViews.Add(x); }
        foreach (var x in archive.ImportProfiles ?? []) { x.UserId = userId; _dbContext.ImportProfiles.Add(x); }
        foreach (var x in archive.Payees ?? []) { x.UserId = userId; _dbContext.Payees.Add(x); }
        foreach (var x in archive.TransferMatches ?? []) { x.UserId = userId; _dbContext.TransferMatches.Add(x); }
        foreach (var x in archive.EntryMetadata ?? []) { x.ExternalKey = x.ExternalKey?.ToUpperInvariant(); x.UserId = userId; _dbContext.EntryMetadata.Add(x); }
        foreach (var x in archive.Reconciliations ?? []) { x.UserId = userId; _dbContext.Reconciliations.Add(x); }
        foreach (var x in archive.RecurringTemplates ?? []) { x.UserId = userId; _dbContext.RecurringTransactionTemplates.Add(x); }
        foreach (var x in archive.CategorizationRules ?? []) { x.User = null; x.AssignCategory = null; x.UserId = userId; _dbContext.CategorizationRules.Add(x); }
        foreach (var x in archive.ExchangeRates ?? []) { x.UserId = userId; _dbContext.ExchangeRates.Add(x); }
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return NoContent();
    }

    private static string? ValidateArchiveReferences(BackupArchiveResponse archive)
    {
        var accountIds = new HashSet<Guid>(archive.Accounts.Select(a => a.Id));
        var categoryIds = new HashSet<Guid>(archive.Categories.Select(c => c.Id));
        var transactionIds = new HashSet<Guid>(archive.Transactions.Select(t => t.Id));
        var savingsGoalIds = new HashSet<Guid>((archive.SavingsGoals ?? []).Select(goal => goal.Id));

        foreach (var transaction in archive.Transactions)
        {
            if (!accountIds.Contains(transaction.AccountId))
            {
                return "Backup contains a transaction referencing an account not present in the archive.";
            }

            if (transaction.CategoryId.HasValue && !categoryIds.Contains(transaction.CategoryId.Value))
            {
                return "Backup contains a transaction referencing a category not present in the archive.";
            }

            if (transaction.ParentTransactionId.HasValue && !transactionIds.Contains(transaction.ParentTransactionId.Value))
            {
                return "Backup contains a transaction referencing a parent transaction not present in the archive.";
            }

            if (transaction.SavingsGoalId.HasValue && !savingsGoalIds.Contains(transaction.SavingsGoalId.Value))
            {
                return "Backup contains a transaction referencing a savings goal not present in the archive.";
            }
        }

        foreach (var period in archive.BudgetPeriods)
        {
            foreach (var limit in period.CategoryLimits)
            {
                if (!categoryIds.Contains(limit.CategoryId))
                {
                    return "Backup contains a budget limit referencing a category not present in the archive.";
                }
            }
        }

        return null;
    }

    private static string? ValidateBookkeeping(BackupArchiveResponse a)
    {
        if (a.InsightPreferences is not null && !a.InsightPreferences.IsValid()) return "Invalid insight preferences in backup.";
        if ((a.InsightDismissals ?? []).GroupBy(x => x.Id).Any(g => g.Count() > 1) || (a.InsightDismissals ?? []).GroupBy(x => x.AlertKey).Any(g => g.Count() > 1)
            || (a.InsightDismissals ?? []).Any(x => x.AlertKey is null || !System.Text.RegularExpressions.Regex.IsMatch(x.AlertKey, "^[0-9A-F]{64}$")
                || !DateOnly.TryParseExact(x.Month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                || date.Year is < 1900 or > 9998 || x.DismissedAtUtc == default || x.DismissedAtUtc.Kind != DateTimeKind.Utc)) return "Invalid insight dismissal in backup.";
        var accounts = a.Accounts.Select(x => x.Id).ToHashSet(); var categories = a.Categories.Select(x => x.Id).ToHashSet();
        var categoryMap = a.Categories.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var category in a.Categories)
        {
            var seen = new HashSet<Guid> { category.Id }; var parent = category.ParentCategoryId;
            while (parent.HasValue)
            {
                if (!seen.Add(parent.Value) || !categoryMap.TryGetValue(parent.Value, out var p) || !p.Kind.Equals(category.Kind, StringComparison.OrdinalIgnoreCase)) return "Invalid category hierarchy in backup.";
                parent = p.ParentCategoryId;
            }
        }
        var entries = a.Transactions.Select(x => x.Id).ToHashSet(); var payees = (a.Payees ?? []).Select(x => x.Id).ToHashSet();
        if ((a.Reconciliations ?? []).GroupBy(x => x.Id).Any(x => x.Count() > 1)
            || (a.EntryMetadata ?? []).GroupBy(x => x.Id).Any(x => x.Count() > 1)
            || (a.ImportProfiles ?? []).GroupBy(x => x.Name).Any(x => x.Count() > 1)
            || (a.Payees ?? []).GroupBy(x => x.Name).Any(x => x.Count() > 1)) return "Duplicate bookkeeping records in backup.";
        if ((a.EntryMetadata ?? []).Where(x => x.ExternalKey != null).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return "Duplicate imported bank identities in backup.";
        if ((a.TransferMatches ?? []).Any(x => !entries.Contains(x.OutgoingId) || !entries.Contains(x.IncomingId) || x.OutgoingId == x.IncomingId
            || x.OutgoingType != Ledgerra.Domain.Transactions.TransactionType.Expense || x.IncomingType is not (Ledgerra.Domain.Transactions.TransactionType.Income or Ledgerra.Domain.Transactions.TransactionType.Refund))) return "Invalid transfer matching history in backup.";
        if ((a.SavedLedgerViews ?? []).Count > 100 || (a.SavedLedgerViews ?? []).GroupBy(x => x.Id).Any(g => g.Count() > 1)
            || (a.SavedLedgerViews ?? []).GroupBy(x => x.Name).Any(g => g.Count() > 1)
            || (a.SavedLedgerViews ?? []).Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 120 || !LedgerToolsController.IsValidViewQuery(x.Query))) return "Invalid saved filters in backup.";
        var statements = (a.Reconciliations ?? []).ToDictionary(x => x.Id);
        if ((a.Payees ?? []).Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 120 || (x.DefaultCategoryId.HasValue && !categories.Contains(x.DefaultCategoryId.Value)))) return "Invalid payee in backup.";
        foreach (var x in a.EntryMetadata ?? [])
        {
            if (x.ExternalKey is not null && !System.Text.RegularExpressions.Regex.IsMatch(x.ExternalKey, "^[0-9A-Fa-f]{64}$")) return "Invalid imported bank identity in backup.";
            if (!entries.Contains(x.Id) || (x.PayeeId.HasValue && !payees.Contains(x.PayeeId.Value)) || x.Status is not ("Uncleared" or "Cleared" or "Reconciled") || x.Reference is null || x.Tags is null || x.PaymentMethod is null || x.Reference.Length > 120 || x.Tags.Length > 400 || x.PaymentMethod.Length > 40) return "Invalid entry metadata in backup.";
            if (x.Status == "Reconciled")
            {
                if (!x.ReconciliationId.HasValue || !statements.TryGetValue(x.ReconciliationId.Value, out var statement) || a.Transactions.Single(t => t.Id == x.Id).AccountId != statement.AccountId) return "Invalid statement reference in backup.";
            }
            else if (x.ReconciliationId.HasValue) return "Invalid statement status in backup.";
        }
        if ((a.Reconciliations ?? []).Any(x => !accounts.Contains(x.AccountId) || x.StatementDate == default)) return "Invalid reconciliation in backup.";
        if ((a.RecurringTemplates ?? []).Any(x => !accounts.Contains(x.AccountId) || (x.DestinationAccountId.HasValue && (!accounts.Contains(x.DestinationAccountId.Value) || x.DestinationAccountId == x.AccountId)) || (x.CategoryId.HasValue && !categories.Contains(x.CategoryId.Value)) || x.Amount <= 0)) return "Invalid recurring template in backup.";
        if ((a.CategorizationRules ?? []).Any(x => !categories.Contains(x.AssignCategoryId))) return "Invalid categorization rule in backup.";
        foreach (var rule in a.CategorizationRules ?? [])
        {
            try
            {
                if (rule.ConditionsJson is null) return "Invalid compound rule in backup.";
                var conditions = System.Text.Json.JsonSerializer.Deserialize<List<Ledgerra.Domain.Imports.ImportRuleCondition>>(rule.ConditionsJson);
                if (conditions is null || conditions.Count > 8 || rule.ConditionsJson.Length > 4000 || conditions.Any(x => !ImportRulesController.IsValidCondition(x))
                    || conditions.Any(x => x.Field.Equals("Account", StringComparison.OrdinalIgnoreCase) && (!Guid.TryParse(x.Value, out var accountId) || !accounts.Contains(accountId)))) return "Invalid compound rule in backup.";
            }
            catch (System.Text.Json.JsonException) { return "Invalid compound rule in backup."; }
        }
        foreach (var schedule in a.RecurringTemplates ?? [])
        {
            if (!Enum.IsDefined(schedule.Type) || !Enum.IsDefined(schedule.Interval) || schedule.Type == Ledgerra.Domain.Transactions.TransactionType.TransferIn
                || (schedule.Type == Ledgerra.Domain.Transactions.TransactionType.TransferOut) != schedule.DestinationAccountId.HasValue)
                return "Invalid recurring transaction type in backup.";
        }
        foreach (var profile in a.ImportProfiles ?? [])
        {
            try
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<ProfileSettings>(profile.SettingsJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (settings is null || settings.Options is null || string.IsNullOrWhiteSpace(settings.DateColumn)) return "Invalid bank profile in backup.";
                settings.Options.Validate();
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { return "Invalid bank profile in backup."; }
        }
        if ((a.ExchangeRates ?? []).Any(x => x.Rate <= 0 || x.FromCurrencyCode.Length != 3 || x.ToCurrencyCode.Length != 3)) return "Invalid exchange rate in backup.";
        if ((a.ImportProfiles ?? []).Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 120 || x.SettingsJson.Length > 8000)) return "Invalid import profile in backup.";
        return null;
    }

    private static string? ValidateArchiveShape(BackupArchiveResponse archive)
    {
        foreach (var account in archive.Accounts)
        {
            if (!Enum.TryParse<Ledgerra.Domain.Accounts.AccountType>(account.Type, ignoreCase: true, out _))
            {
                return "Backup contains an account with an unsupported type.";
            }

            if (!Enum.TryParse<Ledgerra.Domain.Accounts.AccountIconKind>(account.IconKind, ignoreCase: true, out _))
            {
                return "Backup contains an account with an unsupported icon kind.";
            }
        }

        foreach (var category in archive.Categories)
        {
            if (!Enum.TryParse<Ledgerra.Domain.Categories.CategoryKind>(category.Kind, ignoreCase: true, out _))
            {
                return "Backup contains a category with an unsupported kind.";
            }
        }

        foreach (var transaction in archive.Transactions)
        {
            if (!Enum.TryParse<Ledgerra.Domain.Transactions.TransactionType>(transaction.Type, ignoreCase: true, out _))
            {
                return "Backup contains a transaction with an unsupported type.";
            }

            if (!TryParseDateTime(transaction.OccurredOnUtc))
            {
                return "Backup contains a transaction with an invalid occurred-on date.";
            }
        }

        foreach (var goal in archive.SavingsGoals ?? [])
        {
            if (!string.IsNullOrWhiteSpace(goal.DeadlineUtc) && !TryParseDateTime(goal.DeadlineUtc))
            {
                return "Backup contains a savings goal with an invalid deadline date.";
            }

            if (!string.IsNullOrWhiteSpace(goal.CreatedAtUtc) && !TryParseDateTime(goal.CreatedAtUtc))
            {
                return "Backup contains a savings goal with an invalid created date.";
            }

            if (!string.IsNullOrWhiteSpace(goal.UpdatedAtUtc) && !TryParseDateTime(goal.UpdatedAtUtc))
            {
                return "Backup contains a savings goal with an invalid updated date.";
            }
        }

        foreach (var period in archive.BudgetPeriods)
        {
            if (period.Month is < 1 or > 12)
            {
                return "Backup contains a budget period with an invalid month.";
            }
        }

        return null;
    }

    private static bool TryParseDateTime(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }
}
