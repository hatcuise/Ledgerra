using Ledgerra.Domain.Imports;
using System.ComponentModel.DataAnnotations;

namespace Ledgerra.Api.Contracts;

public sealed record ImportRuleResponse(
    Guid Id,
    string Name,
    string MatchField,
    string MatchOperator,
    string MatchValue,
    Guid AssignCategoryId,
    string AssignTransactionType,
    int Priority,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ImportRuleCondition>? Conditions = null);

public sealed class UpsertImportRuleRequest
{
    public IReadOnlyList<ImportRuleCondition>? Conditions { get; init; }
    [Required, MaxLength(120)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public string MatchField { get; init; } = string.Empty;

    [Required]
    public string MatchOperator { get; init; } = string.Empty;

    [Required, MaxLength(200)]
    public string MatchValue { get; init; } = string.Empty;

    [Required]
    public Guid AssignCategoryId { get; init; }

    [Required]
    public string AssignTransactionType { get; init; } = string.Empty;

    public int Priority { get; init; } = 100;

    public bool IsActive { get; init; } = true;
}
