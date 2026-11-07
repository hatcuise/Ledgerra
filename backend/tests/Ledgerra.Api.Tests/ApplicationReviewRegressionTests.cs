using Ledgerra.Application.Budgets;
using Ledgerra.Application.ExchangeRates;
using Ledgerra.Application.Imports;
using Ledgerra.Application.Transactions;
using Ledgerra.Domain.Ai;
using Ledgerra.Domain.Budgets;
using Ledgerra.Domain.Transactions;

namespace Ledgerra.Api.Tests;

public sealed class ApplicationReviewRegressionTests
{
    [Fact]
    public async Task UpdateBudget_RejectsInvalidPeriodBeforeCheckingCategories()
    {
        var store = new RecordingBudgetSummaryStore();
        var handler = new UpdateBudgetCommandHandler(store);

        var result = await handler.HandleAsync(
            new UpdateBudgetCommand(Guid.NewGuid(), 2026, 13, []),
            CancellationToken.None);

        Assert.True(result.HasValidationError);
        Assert.Contains("month", result.ValidationError, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.CategoriesWereChecked);
    }

    [Fact]
    public async Task AnalyzeMonthlyReport_RejectsAiAccountIdThatDoesNotMatchCommandScope()
    {
        var commandAccountId = Guid.NewGuid();
        var analyzer = new StubMonthlyReportAnalyzer(
            new AiDraftAnalysisResult(
                [
                    new AiDraftAnalysisItem(
                        "row-1",
                        Guid.NewGuid().ToString(),
                        null,
                        12.34m,
                        "Expense",
                        "2026-04-10T12:00:00Z",
                        null,
                        0.9m,
                        [])
                ],
                []));
        var handler = new AnalyzeMonthlyReportCommandHandler(
            analyzer,
            new PassthroughMonthlyReportRuleMatcher(),
            new PassthroughMonthlyReportDuplicateMarker());

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new AnalyzeMonthlyReportCommand(
                Guid.NewGuid(),
                commandAccountId,
                "2026-04",
                AiProvider.OpenAi,
                "report"),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateTransaction_RejectsNonPositiveAmount()
    {
        var store = new RecordingTransactionCommandStore
        {
            AccountExists = true
        };
        var handler = new CreateTransactionCommandHandler(store);

        var result = await handler.HandleAsync(
            new CreateTransactionCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                null,
                0m,
                "Expense",
                new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
                null,
                null),
            CancellationToken.None);

        Assert.True(result.HasValidationError);
        Assert.Equal("amount", result.ValidationKey);
        Assert.False(store.CreatedTransaction);
    }

    [Fact]
    public async Task CreateTransaction_RejectsNonUtcOccurredOn()
    {
        var store = new RecordingTransactionCommandStore
        {
            AccountExists = true
        };
        var handler = new CreateTransactionCommandHandler(store);

        var result = await handler.HandleAsync(
            new CreateTransactionCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                null,
                10m,
                "Expense",
                new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Unspecified),
                null,
                null),
            CancellationToken.None);

        Assert.True(result.HasValidationError);
        Assert.Equal("occurredOnUtc", result.ValidationKey);
        Assert.False(store.CreatedTransaction);
    }

    [Fact]
    public async Task UpdateTransaction_ValidatesReplacementBeforeDeletingExistingTransaction()
    {
        var existing = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Amount = 15m,
            Type = TransactionType.Expense,
            OccurredOnUtc = new DateTime(2026, 4, 9, 12, 0, 0, DateTimeKind.Utc)
        };
        var store = new RecordingTransactionCommandStore
        {
            AccountExists = true,
            ExistingTransaction = existing
        };
        var createHandler = new CreateTransactionCommandHandler(store);
        var updateHandler = new UpdateTransactionCommandHandler(store, createHandler);

        var result = await updateHandler.HandleAsync(
            new UpdateTransactionCommand(
                existing.UserId,
                existing.Id,
                null,
                null,
                0m,
                "Expense",
                new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
                null,
                null),
            CancellationToken.None);

        Assert.True(result.HasValidationError);
        Assert.Equal("amount", result.ValidationKey);
        Assert.False(store.DeletedTransaction);
        Assert.False(store.ReplacedTransaction);
    }

    [Fact]
    public async Task UpsertExchangeRate_RejectsNonPositiveRateBeforePersisting()
    {
        var store = new RecordingExchangeRateStore();
        var handler = new UpsertExchangeRateCommandHandler(store);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
            new UpsertExchangeRateCommand(Guid.NewGuid(), "eur", "usd", new DateOnly(2026, 5, 18), 0m),
            CancellationToken.None));

        Assert.Contains("positive", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.UpsertedRate);
    }

    private sealed class RecordingBudgetSummaryStore : IBudgetSummaryStore
    {
        public bool CategoriesWereChecked { get; private set; }

        public Task<BudgetSummary> GetSummaryAsync(Guid userId, int year, int month, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> CategoriesExistAsync(Guid userId, IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken)
        {
            CategoriesWereChecked = true;
            return Task.FromResult(true);
        }

        public Task<BudgetSummary> UpdateAsync(Guid userId, int year, int month, IReadOnlyList<BudgetCategoryLimitInput> categoryLimits, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubMonthlyReportAnalyzer : IMonthlyReportAnalyzer
    {
        private readonly AiDraftAnalysisResult _result;

        public StubMonthlyReportAnalyzer(AiDraftAnalysisResult result)
        {
            _result = result;
        }

        public Task<AiDraftAnalysisResult> AnalyzeAsync(
            Guid userId,
            Guid accountId,
            AiProvider provider,
            string month,
            string reportContent,
            CancellationToken cancellationToken,
            IProgress<MonthlyReportAnalyzerProgress>? progress = null)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class PassthroughMonthlyReportRuleMatcher : IMonthlyReportRuleMatcher
    {
        public Task<IReadOnlyList<MonthlyReportReviewDraft>> ApplyAsync(
            Guid userId,
            IReadOnlyList<MonthlyReportReviewDraft> drafts,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(drafts);
        }
    }

    private sealed class PassthroughMonthlyReportDuplicateMarker : IMonthlyReportDuplicateMarker
    {
        public Task<IReadOnlyList<MonthlyReportReviewDraft>> MarkDuplicatesAsync(
            Guid userId,
            IReadOnlyList<MonthlyReportReviewDraft> drafts,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(drafts);
        }
    }

    private sealed class RecordingTransactionCommandStore : ITransactionCommandStore
    {
        public bool AccountExists { get; init; }

        public Transaction? ExistingTransaction { get; init; }

        public bool CreatedTransaction { get; private set; }

        public bool DeletedTransaction { get; private set; }

        public bool ReplacedTransaction { get; private set; }

        public Task<bool> AccountExistsAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
        {
            return Task.FromResult(AccountExists);
        }

        public Task<bool> AccountsShareCurrencyAsync(Guid userId, Guid sourceAccountId, Guid destinationAccountId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CategoryExistsAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<Transaction?> GetByIdAsync(Guid userId, Guid transactionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(ExistingTransaction);
        }

        public Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken)
        {
            DeletedTransaction = true;
            return Task.CompletedTask;
        }

        public Task DeleteTransferGroupAsync(Guid userId, Guid transferGroupId, CancellationToken cancellationToken)
        {
            DeletedTransaction = true;
            return Task.CompletedTask;
        }

        public Task<Transaction> CreateAsync(
            Transaction transaction,
            CancellationToken cancellationToken,
            IReadOnlyList<TransactionSplitLine>? splitLines = null)
        {
            CreatedTransaction = true;
            return Task.FromResult(transaction);
        }

        public Task<Transaction> MoveToAccountAsync(
            Transaction existing,
            Guid destinationAccountId,
            CancellationToken cancellationToken)
        {
            existing.AccountId = destinationAccountId;
            return Task.FromResult(existing);
        }

        public Task<Transaction> CreateTransferAsync(
            Guid userId,
            Guid sourceAccountId,
            Guid destinationAccountId,
            decimal amount,
            DateTime occurredOnUtc,
            string? note,
            Guid? savingsGoalId,
            CancellationToken cancellationToken)
        {
            CreatedTransaction = true;
            return Task.FromResult(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = sourceAccountId,
                Amount = amount,
                Type = TransactionType.TransferOut,
                OccurredOnUtc = occurredOnUtc,
                Note = note,
                TransferGroupId = Guid.NewGuid(),
                SavingsGoalId = savingsGoalId
            });
        }

        public Task<Transaction> ReplaceAsync(
            Transaction existing,
            Transaction replacement,
            CancellationToken cancellationToken,
            IReadOnlyList<TransactionSplitLine>? splitLines = null)
        {
            ReplacedTransaction = true;
            return Task.FromResult(replacement);
        }

        public Task<Transaction> ReplaceWithTransferAsync(
            Transaction existing,
            Guid destinationAccountId,
            decimal amount,
            DateTime occurredOnUtc,
            string? note,
            Guid? savingsGoalId,
            CancellationToken cancellationToken)
        {
            ReplacedTransaction = true;
            return Task.FromResult(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = existing.UserId,
                AccountId = existing.AccountId,
                Amount = amount,
                Type = TransactionType.TransferOut,
                OccurredOnUtc = occurredOnUtc,
                Note = note,
                TransferGroupId = Guid.NewGuid(),
                SavingsGoalId = savingsGoalId
            });
        }
    }

    private sealed class RecordingExchangeRateStore : IExchangeRateStore
    {
        public bool UpsertedRate { get; private set; }

        public Task<IReadOnlyList<ExchangeRateResult>> GetForUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeRateResult> UpsertAsync(UpsertExchangeRateCommand command, CancellationToken cancellationToken)
        {
            UpsertedRate = true;
            return Task.FromResult(new ExchangeRateResult(
                Guid.NewGuid(),
                command.FromCurrencyCode,
                command.ToCurrencyCode,
                $"{command.Month:yyyy-MM}",
                command.Rate,
                DateTime.UtcNow));
        }

        public Task<bool> DeleteAsync(Guid userId, Guid rateId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
