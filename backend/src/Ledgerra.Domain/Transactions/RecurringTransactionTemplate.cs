namespace Ledgerra.Domain.Transactions;

public sealed class RecurringTransactionTemplate
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public Guid? DestinationAccountId { get; set; }
    public Guid? CategoryId { get; set; }
    public decimal Amount { get; set; }
    public TransactionType Type { get; set; }
    public RecurringInterval Interval { get; set; }
    public DateTime StartOnUtc { get; set; }
    public DateTime? LastGeneratedOnUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Note { get; set; }
}
