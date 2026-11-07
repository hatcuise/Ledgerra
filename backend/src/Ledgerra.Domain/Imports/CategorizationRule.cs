using Ledgerra.Domain.Auth;
using Ledgerra.Domain.Categories;
using Ledgerra.Domain.Transactions;

namespace Ledgerra.Domain.Imports;

public sealed class CategorizationRule
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public ImportRuleMatchField MatchField { get; set; } = ImportRuleMatchField.Note;

    public ImportRuleMatchOperator MatchOperator { get; set; } = ImportRuleMatchOperator.Contains;

    public string ConditionsJson { get; set; } = "[]";

    public string MatchValue { get; set; } = string.Empty;

    public Guid AssignCategoryId { get; set; }

    public TransactionType AssignTransactionType { get; set; } = TransactionType.Expense;

    public int Priority { get; set; } = 100;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public AppUser? User { get; set; }

    public Category? AssignCategory { get; set; }
}

public sealed record ImportRuleCondition(string Field, string Operator, string Value);
