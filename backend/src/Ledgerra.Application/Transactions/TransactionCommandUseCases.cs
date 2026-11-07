using Ledgerra.Application.Reporting;
using Ledgerra.Domain.Transactions;

namespace Ledgerra.Application.Transactions;

public sealed record CreateTransactionCommand(
    Guid UserId,
    Guid AccountId,
    Guid? CategoryId,
    Guid? DestinationAccountId,
    decimal Amount,
    string Type,
    DateTime OccurredOnUtc,
    string? Note,
    Guid? SavingsGoalId,
    IReadOnlyList<TransactionSplitLine>? SplitLines = null);

public sealed record UpdateTransactionCommand(
    Guid UserId,
    Guid TransactionId,
    Guid? CategoryId,
    Guid? DestinationAccountId,
    decimal Amount,
    string Type,
    DateTime OccurredOnUtc,
    string? Note,
    Guid? SavingsGoalId,
    IReadOnlyList<TransactionSplitLine>? SplitLines = null);

public sealed record TransactionSplitLine(Guid CategoryId, decimal Amount);

public sealed record DeleteTransactionCommand(Guid UserId, Guid TransactionId);

public sealed record MoveTransactionAccountCommand(Guid UserId, Guid TransactionId, Guid DestinationAccountId);

public sealed record TransactionDetails(
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

public interface ITransactionCommandStore
{
    Task<bool> AccountExistsAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);

    Task<bool> AccountsShareCurrencyAsync(Guid userId, Guid sourceAccountId, Guid destinationAccountId, CancellationToken cancellationToken);

    Task<bool> CategoryExistsAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken);

    Task<Transaction?> GetByIdAsync(Guid userId, Guid transactionId, CancellationToken cancellationToken);

    Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken);

    Task DeleteTransferGroupAsync(Guid userId, Guid transferGroupId, CancellationToken cancellationToken);

    Task<Transaction> MoveToAccountAsync(
        Transaction existing,
        Guid destinationAccountId,
        CancellationToken cancellationToken);

    Task<Transaction> CreateAsync(
        Transaction transaction,
        CancellationToken cancellationToken,
        IReadOnlyList<TransactionSplitLine>? splitLines = null);

    Task<Transaction> CreateTransferAsync(
        Guid userId,
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        DateTime occurredOnUtc,
        string? note,
        Guid? savingsGoalId,
        CancellationToken cancellationToken);

    Task<Transaction> ReplaceAsync(
        Transaction existing,
        Transaction replacement,
        CancellationToken cancellationToken,
        IReadOnlyList<TransactionSplitLine>? splitLines = null);

    Task<Transaction> ReplaceWithTransferAsync(
        Transaction existing,
        Guid destinationAccountId,
        decimal amount,
        DateTime occurredOnUtc,
        string? note,
        Guid? savingsGoalId,
        CancellationToken cancellationToken);
}

public sealed class CreateTransactionCommandHandler
{
    private readonly ITransactionCommandStore _transactionCommandStore;
    private readonly IMonthlyAccountBalanceSnapshotService? _snapshotService;

    public CreateTransactionCommandHandler(
        ITransactionCommandStore transactionCommandStore,
        IMonthlyAccountBalanceSnapshotService? snapshotService = null)
    {
        _transactionCommandStore = transactionCommandStore;
        _snapshotService = snapshotService;
    }

    public async Task<TransactionCommandResult> HandleAsync(CreateTransactionCommand command, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(command, cancellationToken);

        if (validation is not null)
        {
            return validation;
        }

        if (IsTransfer(command.Type))
        {
            var transfer = await _transactionCommandStore.CreateTransferAsync(
                command.UserId,
                command.AccountId,
                command.DestinationAccountId!.Value,
                command.Amount,
                command.OccurredOnUtc,
                command.Note,
                command.SavingsGoalId,
                cancellationToken);

            await RefreshSnapshotsAsync(command.UserId, command.OccurredOnUtc, null, cancellationToken);
            return TransactionCommandResult.Success(MapTransaction(transfer));
        }

        var transactionType = ParseNonTransferType(command.Type);
        var transaction = await _transactionCommandStore.CreateAsync(
            new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = command.UserId,
                AccountId = command.AccountId,
                CategoryId = command.CategoryId,
                Amount = command.Amount,
                Type = transactionType,
                Note = command.Note,
                OccurredOnUtc = command.OccurredOnUtc,
                SavingsGoalId = command.SavingsGoalId
            },
            cancellationToken,
            command.SplitLines);

        await RefreshSnapshotsAsync(command.UserId, command.OccurredOnUtc, command.AccountId, cancellationToken);
        return TransactionCommandResult.Success(MapTransaction(transaction));
    }

    private async Task RefreshSnapshotsAsync(Guid userId, DateTime occurredOnUtc, Guid? accountId, CancellationToken cancellationToken)
    {
        if (_snapshotService is null)
        {
            return;
        }

        await _snapshotService.RefreshFromAsync(
            userId,
            new DateOnly(occurredOnUtc.Year, occurredOnUtc.Month, 1),
            accountId,
            cancellationToken);
    }

    public async Task<TransactionCommandResult?> ValidateAsync(
        CreateTransactionCommand command,
        CancellationToken cancellationToken)
    {
        var accountExists = await _transactionCommandStore.AccountExistsAsync(command.UserId, command.AccountId, cancellationToken);
        if (!accountExists)
        {
            return TransactionCommandResult.NotFound("Account not found");
        }

        if (command.CategoryId.HasValue)
        {
            var categoryExists = await _transactionCommandStore.CategoryExistsAsync(command.UserId, command.CategoryId.Value, cancellationToken);
            if (!categoryExists)
            {
                return TransactionCommandResult.NotFound("Category not found");
            }
        }

        if (command.Amount <= 0)
        {
            return TransactionCommandResult.ValidationError("amount", "Amount must be greater than zero.");
        }

        if (command.OccurredOnUtc.Kind != DateTimeKind.Utc)
        {
            return TransactionCommandResult.ValidationError("occurredOnUtc", "OccurredOnUtc must be a UTC date/time.");
        }

        if (IsTransfer(command.Type))
        {
            if (!command.DestinationAccountId.HasValue || command.DestinationAccountId.Value == command.AccountId)
            {
                return TransactionCommandResult.ValidationError("destinationAccountId", "Transfers require a different destination account.");
            }

            var destinationExists = await _transactionCommandStore.AccountExistsAsync(command.UserId, command.DestinationAccountId.Value, cancellationToken);
            if (!destinationExists)
            {
                return TransactionCommandResult.NotFound("Destination account not found");
            }

            if (!await _transactionCommandStore.AccountsShareCurrencyAsync(command.UserId, command.AccountId, command.DestinationAccountId.Value, cancellationToken))
                return TransactionCommandResult.ValidationError("destinationAccountId", "Transfers require accounts in the same currency.");
            return null;
        }

        if (!TryParseNonTransferType(command.Type, out _))
        {
            return TransactionCommandResult.ValidationError("type", "Supported transaction types are Income, Expense, Refund, and Transfer.");
        }

        if (command.SplitLines is { Count: > 0 })
        {
            if (!command.Type.Equals("Expense", StringComparison.OrdinalIgnoreCase))
            {
                return TransactionCommandResult.ValidationError("splitLines", "Split lines are supported only for expense transactions.");
            }

            if (command.SplitLines.Sum(item => item.Amount) != command.Amount)
            {
                return TransactionCommandResult.ValidationError("splitLines", "Split lines total must equal the transaction amount.");
            }

            foreach (var splitLine in command.SplitLines)
            {
                var splitCategoryExists = await _transactionCommandStore.CategoryExistsAsync(command.UserId, splitLine.CategoryId, cancellationToken);
                if (!splitCategoryExists)
                {
                    return TransactionCommandResult.NotFound("Split line category not found");
                }
            }
        }

        return null;
    }

    private static bool IsTransfer(string type) => type.Equals("Transfer", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTransferCommand(string type) => IsTransfer(type);

    private static bool TryParseNonTransferType(string type, out TransactionType parsedType)
    {
        if (Enum.TryParse<TransactionType>(type, true, out parsedType) &&
            (parsedType == TransactionType.Income || parsedType == TransactionType.Expense || parsedType == TransactionType.Refund))
        {
            return true;
        }

        parsedType = default;
        return false;
    }

    internal static TransactionType ParseNonTransferType(string type)
    {
        TryParseNonTransferType(type, out var parsedType);
        return parsedType;
    }

    internal static TransactionDetails MapTransaction(Transaction transaction)
    {
        return new TransactionDetails(
            transaction.Id,
            transaction.AccountId,
            transaction.CategoryId,
            transaction.Amount,
            transaction.Type.ToString(),
            transaction.OccurredOnUtc,
            transaction.Note,
            transaction.TransferGroupId,
            transaction.SavingsGoalId,
            transaction.SplitGroupId,
            transaction.ParentTransactionId);
    }
}

public sealed class UpdateTransactionCommandHandler
{
    private readonly ITransactionCommandStore _transactionCommandStore;
    private readonly CreateTransactionCommandHandler _createTransactionCommandHandler;
    private readonly IMonthlyAccountBalanceSnapshotService? _snapshotService;

    public UpdateTransactionCommandHandler(
        ITransactionCommandStore transactionCommandStore,
        CreateTransactionCommandHandler createTransactionCommandHandler,
        IMonthlyAccountBalanceSnapshotService? snapshotService = null)
    {
        _transactionCommandStore = transactionCommandStore;
        _createTransactionCommandHandler = createTransactionCommandHandler;
        _snapshotService = snapshotService;
    }

    public async Task<TransactionCommandResult> HandleAsync(UpdateTransactionCommand command, CancellationToken cancellationToken)
    {
        var existing = await _transactionCommandStore.GetByIdAsync(command.UserId, command.TransactionId, cancellationToken);
        if (existing is null)
        {
            return TransactionCommandResult.NotFound();
        }

        var replacementCommand = new CreateTransactionCommand(
            command.UserId,
            existing.AccountId,
            command.CategoryId,
            command.DestinationAccountId,
            command.Amount,
            command.Type,
            command.OccurredOnUtc,
            command.Note,
            command.SavingsGoalId,
            command.SplitLines);

        var validation = await _createTransactionCommandHandler.ValidateAsync(replacementCommand, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        if (CreateTransactionCommandHandler.IsTransferCommand(replacementCommand.Type))
        {
            var transfer = await _transactionCommandStore.ReplaceWithTransferAsync(
                existing,
                replacementCommand.DestinationAccountId!.Value,
                replacementCommand.Amount,
                replacementCommand.OccurredOnUtc,
                replacementCommand.Note,
                replacementCommand.SavingsGoalId,
                cancellationToken);

            await RefreshSnapshotsAsync(command.UserId, existing.OccurredOnUtc, replacementCommand.OccurredOnUtc, null, cancellationToken);
            return TransactionCommandResult.Success(CreateTransactionCommandHandler.MapTransaction(transfer));
        }

        var transaction = await _transactionCommandStore.ReplaceAsync(
            existing,
            new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = replacementCommand.UserId,
                AccountId = replacementCommand.AccountId,
                CategoryId = replacementCommand.CategoryId,
                Amount = replacementCommand.Amount,
                Type = CreateTransactionCommandHandler.ParseNonTransferType(replacementCommand.Type),
                Note = replacementCommand.Note,
                OccurredOnUtc = replacementCommand.OccurredOnUtc,
                SavingsGoalId = replacementCommand.SavingsGoalId
            },
            cancellationToken,
            replacementCommand.SplitLines);

        await RefreshSnapshotsAsync(command.UserId, existing.OccurredOnUtc, replacementCommand.OccurredOnUtc, existing.AccountId, cancellationToken);
        return TransactionCommandResult.Success(CreateTransactionCommandHandler.MapTransaction(transaction));
    }

    private async Task RefreshSnapshotsAsync(
        Guid userId,
        DateTime previousOccurredOnUtc,
        DateTime nextOccurredOnUtc,
        Guid? accountId,
        CancellationToken cancellationToken)
    {
        if (_snapshotService is null)
        {
            return;
        }

        var refreshFrom = previousOccurredOnUtc <= nextOccurredOnUtc ? previousOccurredOnUtc : nextOccurredOnUtc;
        await _snapshotService.RefreshFromAsync(
            userId,
            new DateOnly(refreshFrom.Year, refreshFrom.Month, 1),
            accountId,
            cancellationToken);
    }
}

public sealed class DeleteTransactionCommandHandler
{
    private readonly ITransactionCommandStore _transactionCommandStore;
    private readonly IMonthlyAccountBalanceSnapshotService? _snapshotService;

    public DeleteTransactionCommandHandler(
        ITransactionCommandStore transactionCommandStore,
        IMonthlyAccountBalanceSnapshotService? snapshotService = null)
    {
        _transactionCommandStore = transactionCommandStore;
        _snapshotService = snapshotService;
    }

    public async Task<TransactionDeleteResult> HandleAsync(DeleteTransactionCommand command, CancellationToken cancellationToken)
    {
        var transaction = await _transactionCommandStore.GetByIdAsync(command.UserId, command.TransactionId, cancellationToken);
        if (transaction is null)
        {
            return TransactionDeleteResult.NotFound();
        }

        if (transaction.TransferGroupId.HasValue)
        {
            await _transactionCommandStore.DeleteTransferGroupAsync(command.UserId, transaction.TransferGroupId.Value, cancellationToken);
        }
        else
        {
            await _transactionCommandStore.DeleteAsync(transaction, cancellationToken);
        }

        if (_snapshotService is not null)
        {
            var affectedAccountId = transaction.TransferGroupId.HasValue ? null : (Guid?)transaction.AccountId;
            await _snapshotService.RefreshFromAsync(
                command.UserId,
                new DateOnly(transaction.OccurredOnUtc.Year, transaction.OccurredOnUtc.Month, 1),
                affectedAccountId,
                cancellationToken);
        }

        return TransactionDeleteResult.Success();
    }
}

public sealed class MoveTransactionAccountCommandHandler
{
    private readonly ITransactionCommandStore _transactionCommandStore;
    private readonly IMonthlyAccountBalanceSnapshotService? _snapshotService;

    public MoveTransactionAccountCommandHandler(
        ITransactionCommandStore transactionCommandStore,
        IMonthlyAccountBalanceSnapshotService? snapshotService = null)
    {
        _transactionCommandStore = transactionCommandStore;
        _snapshotService = snapshotService;
    }

    public async Task<TransactionCommandResult> HandleAsync(MoveTransactionAccountCommand command, CancellationToken cancellationToken)
    {
        var existing = await _transactionCommandStore.GetByIdAsync(command.UserId, command.TransactionId, cancellationToken);
        if (existing is null)
        {
            return TransactionCommandResult.NotFound();
        }

        if (existing.TransferGroupId.HasValue)
        {
            return TransactionCommandResult.ValidationError("transactionId", "Transfer transactions cannot be moved to another account.");
        }

        if (existing.AccountId == command.DestinationAccountId)
        {
            return TransactionCommandResult.ValidationError("destinationAccountId", "Destination account must be different from the current account.");
        }

        var destinationExists = await _transactionCommandStore.AccountExistsAsync(command.UserId, command.DestinationAccountId, cancellationToken);
        if (!destinationExists)
        {
            return TransactionCommandResult.NotFound("Destination account not found");
        }

        if (!await _transactionCommandStore.AccountsShareCurrencyAsync(command.UserId, existing.AccountId, command.DestinationAccountId, cancellationToken))
            return TransactionCommandResult.ValidationError("destinationAccountId", "Moving a transaction cannot change its currency. Choose an account with the same currency.");

        var previousAccountId = existing.AccountId;
        var transaction = await _transactionCommandStore.MoveToAccountAsync(existing, command.DestinationAccountId, cancellationToken);

        if (_snapshotService is not null)
        {
            var refreshFrom = new DateOnly(existing.OccurredOnUtc.Year, existing.OccurredOnUtc.Month, 1);
            await _snapshotService.RefreshFromAsync(command.UserId, refreshFrom, previousAccountId, cancellationToken);
            await _snapshotService.RefreshFromAsync(command.UserId, refreshFrom, command.DestinationAccountId, cancellationToken);
        }

        return TransactionCommandResult.Success(CreateTransactionCommandHandler.MapTransaction(transaction));
    }
}

public sealed class TransactionCommandResult
{
    private TransactionCommandResult(TransactionDetails? transaction, string? notFoundTitle, string? validationKey, string? validationMessage)
    {
        Transaction = transaction;
        NotFoundTitle = notFoundTitle;
        ValidationKey = validationKey;
        ValidationMessage = validationMessage;
    }

    public TransactionDetails? Transaction { get; }

    public string? NotFoundTitle { get; }

    public string? ValidationKey { get; }

    public string? ValidationMessage { get; }

    public bool IsNotFound => NotFoundTitle is not null;

    public bool HasValidationError => ValidationKey is not null;

    public static TransactionCommandResult Success(TransactionDetails transaction) => new(transaction, null, null, null);

    public static TransactionCommandResult NotFound(string title = "Transaction not found") => new(null, title, null, null);

    public static TransactionCommandResult ValidationError(string key, string message) => new(null, null, key, message);
}

public sealed class TransactionDeleteResult
{
    private TransactionDeleteResult(bool wasDeleted)
    {
        WasDeleted = wasDeleted;
    }

    public bool WasDeleted { get; }

    public static TransactionDeleteResult Success() => new(true);

    public static TransactionDeleteResult NotFound() => new(false);
}
