using System.ComponentModel.DataAnnotations;

namespace Ledgerra.Api.Contracts;

public sealed class CreateRecurringTransactionTemplateRequest
{
    [Required] public Guid AccountId { get; init; }
    public Guid? DestinationAccountId { get; init; }
    public Guid? CategoryId { get; init; }
    [Range(0.01, 999999999)] public decimal Amount { get; init; }
    [Required] public string Type { get; init; } = string.Empty;
    [Required] public string Interval { get; init; } = string.Empty;
    [Required] public DateTime StartOnUtc { get; init; }
    [MaxLength(400)] public string? Note { get; init; }
}

public sealed class UpdateRecurringTransactionTemplateRequest
{
    [Required] public Guid AccountId { get; init; }
    public Guid? DestinationAccountId { get; init; }
    public Guid? CategoryId { get; init; }
    [Range(0.01, 999999999)] public decimal Amount { get; init; }
    [Required] public string Type { get; init; } = string.Empty;
    [Required] public string Interval { get; init; } = string.Empty;
    [Required] public DateTime StartOnUtc { get; init; }
    public bool IsActive { get; init; } = true;
    [MaxLength(400)] public string? Note { get; init; }
}

public sealed class UpdateRecurringTransactionTemplateStatusRequest
{
    public bool IsActive { get; init; }
}

public sealed record RecurringTransactionTemplateResponse(
    Guid Id,
    Guid AccountId,
    Guid? CategoryId,
    decimal Amount,
    string Type,
    string Interval,
    DateTime StartOnUtc,
    DateTime? LastGeneratedOnUtc,
    bool IsActive,
    string? Note,
    Guid? DestinationAccountId = null);
