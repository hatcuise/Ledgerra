using System.ComponentModel.DataAnnotations;

namespace Ledgerra.Api.Contracts;

public sealed class CreateTransactionRequest : TransactionMetadataRequest
{
    [Required]
    public Guid AccountId { get; init; }

    public Guid? CategoryId { get; init; }

    public Guid? DestinationAccountId { get; init; }

    public Guid? SavingsGoalId { get; init; }

    [Range(0.01, 999999999)]
    public decimal Amount { get; init; }

    [Required]
    public string Type { get; init; } = string.Empty;

    [Required]
    public DateTime OccurredOnUtc { get; init; }

    [MaxLength(400)]
    public string? Note { get; init; }

    public IReadOnlyList<TransactionSplitLineRequest>? SplitLines { get; init; }
}

public sealed class UpdateTransactionRequest : TransactionMetadataRequest
{
    public Guid? CategoryId { get; init; }

    public Guid? DestinationAccountId { get; init; }

    public Guid? SavingsGoalId { get; init; }

    [Range(0.01, 999999999)]
    public decimal Amount { get; init; }

    [Required]
    public string Type { get; init; } = string.Empty;

    [Required]
    public DateTime OccurredOnUtc { get; init; }

    [MaxLength(400)]
    public string? Note { get; init; }

    public IReadOnlyList<TransactionSplitLineRequest>? SplitLines { get; init; }
}

public sealed class MoveTransactionAccountRequest
{
    [Required]
    public Guid DestinationAccountId { get; init; }
}

public abstract class TransactionMetadataRequest
{
    // Null leaves existing metadata intact for older clients.
    public Guid? PayeeId { get; init; }
    public bool UpdateMetadata { get; init; }
    [MaxLength(120)] public string Reference { get; init; } = "";
    [MaxLength(400)] public string Tags { get; init; } = "";
    [MaxLength(40)] public string PaymentMethod { get; init; } = "";
    public string Status { get; init; } = "Uncleared";
}

public sealed class TransactionSplitLineRequest
{
    [Required]
    public Guid CategoryId { get; init; }

    [Range(0.01, 999999999)]
    public decimal Amount { get; init; }
}

public sealed record TransactionResponse(
    Guid Id,
    Guid AccountId,
    Guid? CategoryId,
    decimal Amount,
    string Type,
    DateTime OccurredOnUtc,
    string? Note,
    Guid? TransferGroupId,
    Guid? SavingsGoalId,
    Guid? SplitGroupId,
    Guid? ParentTransactionId);
