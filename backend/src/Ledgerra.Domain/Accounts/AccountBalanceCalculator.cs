using Ledgerra.Domain.Transactions;

namespace Ledgerra.Domain.Accounts;

public static class AccountBalanceCalculator
{
    public static decimal Calculate(Account account, IEnumerable<Transaction> transactions)
    {
        return account.OpeningBalance + transactions.Sum(GetSignedAmount);
    }

    private static decimal GetSignedAmount(Transaction transaction) => TransactionSemantics.BalanceAmount(transaction);
}
