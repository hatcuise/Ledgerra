using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using Ledgerra.Domain.Imports;
using Ledgerra.Domain.Transactions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Services.Imports;

public sealed class ImportCategorizationRuleMatcher : IImportCategorizationRuleMatcher
{
    private readonly LedgerraDbContext _dbContext;

    public ImportCategorizationRuleMatcher(LedgerraDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ImportDraftReviewItem>> ApplyAsync(
        Guid userId,
        IReadOnlyList<ImportDraftReviewItem> drafts,
        CancellationToken cancellationToken)
    {
        var rules = await _dbContext.CategorizationRules
            .Where(rule => rule.UserId == userId && rule.IsActive)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Name)
            .ToListAsync(cancellationToken);

        if (rules.Count == 0)
        {
            return drafts;
        }

        return drafts.Select(draft => ApplyFirstMatchingRule(draft, rules)).ToList();
    }

    private static ImportDraftReviewItem ApplyFirstMatchingRule(ImportDraftReviewItem draft, IReadOnlyList<CategorizationRule> rules)
    {
        var rule = rules.FirstOrDefault(item => Matches(draft, item));
        if (rule is null)
        {
            return draft;
        }

        return draft with
        {
            CategoryId = rule.AssignCategoryId,
            // A merchant's expense-category rule also categorizes its refunds;
            // changing a refund to an expense must use an explicit type rule.
            Type = draft.Type.Equals("Refund", StringComparison.OrdinalIgnoreCase)
                && rule.AssignTransactionType == TransactionType.Expense && rule.MatchField != ImportRuleMatchField.Type && !(JsonSerializer.Deserialize<List<ImportRuleCondition>>(rule.ConditionsJson) ?? []).Any(x => x.Field.Equals("Type", StringComparison.OrdinalIgnoreCase))
                ? "Refund" : rule.AssignTransactionType.ToString(),
            AppliedRuleId = rule.Id,
            AppliedRuleName = rule.Name
        };
    }

    private static bool Matches(ImportDraftReviewItem draft, CategorizationRule rule)
    {
        if (!MatchesSingle(draft, rule)) return false;
        try
        {
            var conditions = JsonSerializer.Deserialize<List<ImportRuleCondition>>(rule.ConditionsJson) ?? [];
            return conditions.All(x => Enum.TryParse<ImportRuleMatchField>(x.Field, true, out var field)
                && Enum.TryParse<ImportRuleMatchOperator>(x.Operator, true, out var op)
                && MatchesSingle(draft, new CategorizationRule { MatchField = field, MatchOperator = op, MatchValue = x.Value }));
        }
        catch (JsonException) { return false; }
    }

    private static bool MatchesSingle(ImportDraftReviewItem draft, CategorizationRule rule)
    {
        return rule.MatchField switch
        {
            ImportRuleMatchField.Note => MatchText(draft.Note, rule.MatchOperator, rule.MatchValue),
            ImportRuleMatchField.Type => MatchType(draft.Type, rule.MatchOperator, rule.MatchValue),
            ImportRuleMatchField.Amount => MatchNumeric(draft.Amount, rule.MatchOperator, rule.MatchValue),
            ImportRuleMatchField.Account => MatchGuid(draft.AccountId, rule.MatchOperator, rule.MatchValue),
            ImportRuleMatchField.Date => MatchDate(draft.OccurredOnUtc.Date, rule.MatchOperator, rule.MatchValue),
            ImportRuleMatchField.Month => MatchMonth(draft.OccurredOnUtc, rule.MatchOperator, rule.MatchValue),
            _ => false
        };
    }

    private static bool MatchText(string? input, ImportRuleMatchOperator op, string value)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return op switch
        {
            ImportRuleMatchOperator.Contains => input.Contains(value, StringComparison.OrdinalIgnoreCase),
            ImportRuleMatchOperator.NotContains => !input.Contains(value, StringComparison.OrdinalIgnoreCase),
            ImportRuleMatchOperator.Equals => string.Equals(input.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase),
            ImportRuleMatchOperator.NotEquals => !string.Equals(input.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase),
            ImportRuleMatchOperator.StartsWith => input.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            ImportRuleMatchOperator.Regex => TryMatchRegex(input, value),
            _ => false
        };
    }

    private static bool TryMatchRegex(string input, string value)
    {
        try
        {
            return Regex.IsMatch(input, value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool MatchType(string input, ImportRuleMatchOperator op, string value)
    {
        if (!Enum.TryParse<TransactionType>(input, true, out var draftType)) return false;
        if (!Enum.TryParse<TransactionType>(value, true, out var expectedType)) return false;
        return op switch
        {
            ImportRuleMatchOperator.Equals => draftType == expectedType,
            ImportRuleMatchOperator.NotEquals => draftType != expectedType,
            _ => false
        };
    }

    private static bool MatchNumeric(decimal input, ImportRuleMatchOperator op, string value)
    {
        return op switch
        {
            ImportRuleMatchOperator.Between => TryParseDecimalPair(value, out var min, out var max) && input >= min && input <= max,
            _ => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var operand) && CompareDecimal(input, op, operand)
        };
    }

    private static bool CompareDecimal(decimal input, ImportRuleMatchOperator op, decimal operand) => op switch
    {
        ImportRuleMatchOperator.Equals => input == operand,
        ImportRuleMatchOperator.NotEquals => input != operand,
        ImportRuleMatchOperator.GreaterThan => input > operand,
        ImportRuleMatchOperator.LessThan => input < operand,
        _ => false
    };

    private static bool MatchGuid(Guid input, ImportRuleMatchOperator op, string value)
    {
        if (!Guid.TryParse(value, out var operand)) return false;
        return op switch
        {
            ImportRuleMatchOperator.Equals => input == operand,
            ImportRuleMatchOperator.NotEquals => input != operand,
            _ => false
        };
    }

    private static bool MatchDate(DateTime input, ImportRuleMatchOperator op, string value)
    {
        return op switch
        {
            ImportRuleMatchOperator.Between => TryParseDatePair(value, out var min, out var max) && input >= min && input <= max,
            _ => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var operand) && CompareDate(input, op, operand.Date)
        };
    }

    private static bool CompareDate(DateTime input, ImportRuleMatchOperator op, DateTime operand) => op switch
    {
        ImportRuleMatchOperator.Equals => input == operand,
        ImportRuleMatchOperator.NotEquals => input != operand,
        ImportRuleMatchOperator.GreaterThan => input > operand,
        ImportRuleMatchOperator.LessThan => input < operand,
        _ => false
    };

    private static bool MatchMonth(DateTime input, ImportRuleMatchOperator op, string value)
    {
        if (!int.TryParse(value, out var month) || month is < 1 or > 12) return false;
        return op switch
        {
            ImportRuleMatchOperator.Equals => input.Month == month,
            ImportRuleMatchOperator.NotEquals => input.Month != month,
            _ => false
        };
    }

    private static bool TryParseDecimalPair(string value, out decimal min, out decimal max)
    {
        var pieces = value.Split("..", StringSplitOptions.TrimEntries);
        if (pieces.Length == 2 &&
            decimal.TryParse(pieces[0], NumberStyles.Number, CultureInfo.InvariantCulture, out min) &&
            decimal.TryParse(pieces[1], NumberStyles.Number, CultureInfo.InvariantCulture, out max))
        {
            if (min > max) (min, max) = (max, min);
            return true;
        }

        min = default;
        max = default;
        return false;
    }

    private static bool TryParseDatePair(string value, out DateTime min, out DateTime max)
    {
        var pieces = value.Split("..", StringSplitOptions.TrimEntries);
        if (pieces.Length == 2 &&
            DateTime.TryParse(pieces[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out min) &&
            DateTime.TryParse(pieces[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out max))
        {
            min = min.Date;
            max = max.Date;
            if (min > max) (min, max) = (max, min);
            return true;
        }

        min = default;
        max = default;
        return false;
    }
}
