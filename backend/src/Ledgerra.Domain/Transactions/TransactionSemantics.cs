namespace Ledgerra.Domain.Transactions;

public static class TransactionSemantics
{
    public static bool IsSpending(Transaction transaction) => transaction.Type is TransactionType.Expense or TransactionType.Refund;
    public static decimal SpendingAmount(Transaction transaction) => transaction.Type == TransactionType.Refund ? -transaction.Amount : transaction.Amount;
    public static decimal BalanceAmount(Transaction transaction) => transaction.ParentTransactionId.HasValue ? 0m : transaction.Type switch
    {
        TransactionType.Income or TransactionType.Refund or TransactionType.TransferIn => transaction.Amount,
        TransactionType.Expense or TransactionType.TransferOut => -transaction.Amount,
        _ => 0m
    };
}
