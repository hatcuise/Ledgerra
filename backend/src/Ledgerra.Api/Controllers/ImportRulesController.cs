using System.Text.Json;
using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Domain.Categories;
using Ledgerra.Domain.Imports;
using Ledgerra.Domain.Transactions;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/import-rules")]
public sealed class ImportRulesController : ControllerBase
{
    private readonly LedgerraDbContext _dbContext;

    public ImportRulesController(LedgerraDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ImportRuleResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var rules = await _dbContext.CategorizationRules
            .Where(rule => rule.UserId == userId)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Name)
            .ToListAsync(cancellationToken);

        return Ok(rules.Select(MapRule).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ImportRuleResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var rule = await _dbContext.CategorizationRules.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == id,
            cancellationToken);

        return rule is null ? NotFound() : Ok(MapRule(rule));
    }

    [HttpPost]
    public async Task<ActionResult<ImportRuleResponse>> Create(UpsertImportRuleRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var validation = await ValidateRequestAsync(userId, request, null, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        TryParseSupportedMatchField(request.MatchField, out var matchField);
        TryParseSupportedMatchOperator(request.MatchOperator, out var matchOperator);
        EnumParsingExtensions.TryParseTransactionType(request.AssignTransactionType, out var transactionType);

        var now = DateTime.UtcNow;
        var rule = new CategorizationRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name.Trim(),
            MatchField = matchField,
            MatchOperator = matchOperator,
            MatchValue = request.MatchValue.Trim(),
            ConditionsJson = JsonSerializer.Serialize(request.Conditions ?? []),
            AssignCategoryId = request.AssignCategoryId,
            AssignTransactionType = transactionType,
            Priority = request.Priority,
            IsActive = request.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _dbContext.CategorizationRules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = rule.Id }, MapRule(rule));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ImportRuleResponse>> Update(Guid id, UpsertImportRuleRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var rule = await _dbContext.CategorizationRules.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == id,
            cancellationToken);

        if (rule is null)
        {
            return NotFound();
        }

        var validation = await ValidateRequestAsync(userId, request, id, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        TryParseSupportedMatchField(request.MatchField, out var matchField);
        TryParseSupportedMatchOperator(request.MatchOperator, out var matchOperator);
        EnumParsingExtensions.TryParseTransactionType(request.AssignTransactionType, out var transactionType);

        rule.Name = request.Name.Trim();
        rule.MatchField = matchField;
        rule.MatchOperator = matchOperator;
        rule.MatchValue = request.MatchValue.Trim();
        if (request.Conditions is not null) rule.ConditionsJson = JsonSerializer.Serialize(request.Conditions);
        rule.AssignCategoryId = request.AssignCategoryId;
        rule.AssignTransactionType = transactionType;
        rule.Priority = request.Priority;
        rule.IsActive = request.IsActive;
        rule.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapRule(rule));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var rule = await _dbContext.CategorizationRules.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == id,
            cancellationToken);

        if (rule is null)
        {
            return NotFound();
        }

        _dbContext.CategorizationRules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<ObjectResult?> ValidateRequestAsync(
        Guid userId,
        UpsertImportRuleRequest request,
        Guid? currentRuleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["name"] = ["Rule name is required."] });
        }

        if (string.IsNullOrWhiteSpace(request.MatchValue))
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["matchValue"] = ["Match value is required."] });
        }

        if (!TryParseSupportedMatchField(request.MatchField, out var matchField))
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["matchField"] = ["Unsupported match field."] });
        }

        if (!TryParseSupportedMatchOperator(request.MatchOperator, out var matchOperator))
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["matchOperator"] = ["Unsupported match operator."] });
        }

        if (!IsSupportedOperatorForField(matchField, matchOperator))
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["matchOperator"] = ["Operator is not supported for the selected field."] });
        }

        if (!EnumParsingExtensions.TryParseTransactionType(request.AssignTransactionType, out var transactionType) ||
            (transactionType != TransactionType.Income && transactionType != TransactionType.Expense && transactionType != TransactionType.Refund))
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["assignTransactionType"] = ["Supported assigned transaction types are Income, Expense, and Refund."] });
        }

        if (request.Conditions is not null)
        {
            if (request.Conditions.Count > 8 || request.Conditions.Any(x => !IsValidCondition(x)))
                return this.ValidationError(new Dictionary<string, string[]> { ["conditions"] = ["Use at most eight valid additional conditions."] });
            foreach (var condition in request.Conditions.Where(x => x.Field.Equals("Account", StringComparison.OrdinalIgnoreCase)))
                if (!Guid.TryParse(condition.Value, out var accountId) || !await _dbContext.Accounts.AnyAsync(x => x.UserId == userId && x.Id == accountId, cancellationToken))
                    return NotFound(new ProblemDetails { Title = "Condition account not found." });
        }
        var trimmedName = request.Name.Trim();
        var duplicateNameExists = await _dbContext.CategorizationRules.AnyAsync(
            rule => rule.UserId == userId &&
                rule.Name == trimmedName &&
                (!currentRuleId.HasValue || rule.Id != currentRuleId.Value),
            cancellationToken);

        if (duplicateNameExists)
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["name"] = ["An import rule with this name already exists."] });
        }

        var category = await _dbContext.Categories.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == request.AssignCategoryId,
            cancellationToken);

        if (category is null)
        {
            return NotFound(new ProblemDetails { Title = "Category not found" }) as ObjectResult;
        }

        if ((transactionType == TransactionType.Income && category.Kind != CategoryKind.Income) ||
            ((transactionType == TransactionType.Expense || transactionType == TransactionType.Refund) && category.Kind != CategoryKind.Expense))
        {
            return this.ValidationError(new Dictionary<string, string[]> { ["assignCategoryId"] = ["Assigned category kind must match the assigned transaction type."] });
        }

        return null;
    }

    internal static bool IsValidCondition(ImportRuleCondition? condition) => condition is not null
        && !string.IsNullOrWhiteSpace(condition.Value) && condition.Value.Length <= 200
        && TryParseSupportedMatchField(condition.Field, out var field)
        && TryParseSupportedMatchOperator(condition.Operator, out var op) && IsSupportedOperatorForField(field, op);

    private static bool TryParseSupportedMatchField(string value, out ImportRuleMatchField field)
    {
        return Enum.TryParse(value, true, out field) &&
            Enum.IsDefined(typeof(ImportRuleMatchField), field);
    }

    private static bool TryParseSupportedMatchOperator(string value, out ImportRuleMatchOperator matchOperator)
    {
        return Enum.TryParse(value, true, out matchOperator) &&
            Enum.IsDefined(typeof(ImportRuleMatchOperator), matchOperator);
    }

    private static bool IsSupportedOperatorForField(ImportRuleMatchField field, ImportRuleMatchOperator matchOperator)
    {
        return field switch
        {
            ImportRuleMatchField.Note => matchOperator is ImportRuleMatchOperator.Contains or ImportRuleMatchOperator.NotContains or ImportRuleMatchOperator.Equals or ImportRuleMatchOperator.NotEquals or ImportRuleMatchOperator.StartsWith or ImportRuleMatchOperator.Regex,
            ImportRuleMatchField.Type => matchOperator is ImportRuleMatchOperator.Equals or ImportRuleMatchOperator.NotEquals,
            ImportRuleMatchField.Amount => matchOperator is ImportRuleMatchOperator.Equals or ImportRuleMatchOperator.NotEquals or ImportRuleMatchOperator.GreaterThan or ImportRuleMatchOperator.LessThan or ImportRuleMatchOperator.Between,
            ImportRuleMatchField.Account => matchOperator is ImportRuleMatchOperator.Equals or ImportRuleMatchOperator.NotEquals,
            ImportRuleMatchField.Date => matchOperator is ImportRuleMatchOperator.Equals or ImportRuleMatchOperator.NotEquals or ImportRuleMatchOperator.GreaterThan or ImportRuleMatchOperator.LessThan or ImportRuleMatchOperator.Between,
            ImportRuleMatchField.Month => matchOperator is ImportRuleMatchOperator.Equals or ImportRuleMatchOperator.NotEquals,
            _ => false
        };
    }

    private static ImportRuleResponse MapRule(CategorizationRule rule)
    {
        return new ImportRuleResponse(
            rule.Id,
            rule.Name,
            rule.MatchField.ToString(),
            rule.MatchOperator.ToString(),
            rule.MatchValue,
            rule.AssignCategoryId,
            rule.AssignTransactionType.ToString(),
            rule.Priority,
            rule.IsActive,
            rule.CreatedAtUtc,
            rule.UpdatedAtUtc,
            JsonSerializer.Deserialize<List<ImportRuleCondition>>(rule.ConditionsJson) ?? []);
    }
}
