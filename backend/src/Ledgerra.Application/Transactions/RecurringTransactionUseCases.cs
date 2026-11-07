using Ledgerra.Domain.Transactions;

namespace Ledgerra.Application.Transactions;

public interface IRecurringTransactionRepository
{
    Task<IReadOnlyList<RecurringTransactionTemplate>> GetAllAsync(Guid userId, CancellationToken ct);
    Task<RecurringTransactionTemplate?> GetByIdAsync(Guid userId, Guid templateId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> GetUserIdsWithActiveTemplatesAsync(CancellationToken ct);
    Task<RecurringTransactionTemplate> CreateAsync(RecurringTransactionTemplate template, CancellationToken ct);
    Task<bool> AccountExistsAsync(Guid userId, Guid accountId, CancellationToken ct);
    Task<bool> CategoryExistsAsync(Guid userId, Guid categoryId, CancellationToken ct);
    Task<IReadOnlyList<RecurringTransactionTemplate>> GetActiveTemplatesAsync(Guid userId, CancellationToken ct);
    Task AddTransactionAsync(Transaction transaction, CancellationToken ct);
    Task DeleteAsync(RecurringTransactionTemplate template, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task<T> ExecuteInSerializableTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct);
}

public sealed class RecurringTransactionUseCases
{
    private const int MaxCatchUpPerTemplate = 100;
    private const int DefaultGenerationBatchSize = 50;
    private readonly IRecurringTransactionRepository _repository;

    public RecurringTransactionUseCases(IRecurringTransactionRepository repository) => _repository = repository;

    public async Task<IReadOnlyList<RecurringTransactionTemplate>> GetAllAsync(Guid userId, CancellationToken ct)
        => await _repository.GetAllAsync(userId, ct);

    public async Task<RecurringTransactionTemplate> CreateAsync(Guid userId, Guid accountId, Guid? categoryId, decimal amount, string type, string interval, DateTime startOnUtc, string? note, CancellationToken ct, Guid? destinationAccountId = null)
    {
        var (txType, parsedInterval, normalizedStart) = await ValidateTemplateFieldsAsync(userId, accountId, categoryId, amount, type, interval, startOnUtc, ct, destinationAccountId);

        var item = new RecurringTransactionTemplate
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = accountId,
            CategoryId = txType == TransactionType.TransferOut ? null : categoryId,
            DestinationAccountId = txType == TransactionType.TransferOut ? destinationAccountId : null,
            Amount = amount,
            Type = txType,
            Interval = parsedInterval,
            StartOnUtc = normalizedStart,
            Note = note
        };
        return await _repository.CreateAsync(item, ct);
    }

    public async Task<RecurringTransactionTemplate> UpdateAsync(Guid userId, Guid templateId, Guid accountId, Guid? categoryId, decimal amount, string type, string interval, DateTime startOnUtc, bool isActive, string? note, CancellationToken ct, Guid? destinationAccountId = null)
    {
        var template = await GetRequiredTemplateAsync(userId, templateId, ct);
        var (txType, parsedInterval, normalizedStart) = await ValidateTemplateFieldsAsync(userId, accountId, categoryId, amount, type, interval, startOnUtc, ct, destinationAccountId);

        var scheduleChanged = template.StartOnUtc != normalizedStart || template.Interval != parsedInterval;
        template.AccountId = accountId;
        template.CategoryId = txType == TransactionType.TransferOut ? null : categoryId;
        template.DestinationAccountId = txType == TransactionType.TransferOut ? destinationAccountId : null;
        template.Amount = amount;
        template.Type = txType;
        template.Interval = parsedInterval;
        template.StartOnUtc = normalizedStart;
        template.IsActive = isActive;
        template.Note = note;
        if (scheduleChanged)
        {
            template.LastGeneratedOnUtc = null;
        }

        await _repository.SaveChangesAsync(ct);
        return template;
    }

    public async Task<RecurringTransactionTemplate> SetActiveAsync(Guid userId, Guid templateId, bool isActive, CancellationToken ct)
    {
        var template = await GetRequiredTemplateAsync(userId, templateId, ct);
        template.IsActive = isActive;
        await _repository.SaveChangesAsync(ct);
        return template;
    }

    public async Task DeleteAsync(Guid userId, Guid templateId, CancellationToken ct)
    {
        var template = await GetRequiredTemplateAsync(userId, templateId, ct);
        await _repository.DeleteAsync(template, ct);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task<int> GenerateDueForAllUsersAsync(DateTime nowUtc, CancellationToken ct)
        => await GenerateDueForUsersBatchAsync(nowUtc, DefaultGenerationBatchSize, ct);

    public async Task<int> GenerateDueForUsersBatchAsync(DateTime nowUtc, int batchSize, CancellationToken ct)
    {
        var userIds = await _repository.GetUserIdsWithActiveTemplatesAsync(ct);
        var generated = 0;
        foreach (var batch in userIds.Chunk(Math.Max(1, batchSize)))
        {
            foreach (var userId in batch)
            {
                generated += await GenerateDueAsync(userId, nowUtc, ct);
            }
        }

        return generated;
    }

    public async Task<int> GenerateDueAsync(Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        return await _repository.ExecuteInSerializableTransactionAsync(async () =>
        {
            var templates = await _repository.GetActiveTemplatesAsync(userId, ct);
            var generated = 0;
            foreach (var template in templates)
            {
                var catchUpCount = 0;
                var next = template.LastGeneratedOnUtc ?? template.StartOnUtc;
                if (template.LastGeneratedOnUtc.HasValue)
                {
                    next = template.Interval == RecurringInterval.Weekly
                        ? next.AddDays(7)
                        : GetNextAnchoredMonthlyOccurrence(template.StartOnUtc, next);
                }
                while (next <= nowUtc && catchUpCount < MaxCatchUpPerTemplate)
                {
                    var group = template.DestinationAccountId.HasValue ? Guid.NewGuid() : (Guid?)null;
                    await _repository.AddTransactionAsync(new Transaction { Id = Guid.NewGuid(), UserId = userId, AccountId = template.AccountId, CategoryId = template.CategoryId, Amount = template.Amount, Type = template.Type, OccurredOnUtc = next, Note = template.Note, TransferGroupId = group }, ct);
                    if (template.DestinationAccountId is Guid destination)
                        await _repository.AddTransactionAsync(new Transaction { Id = Guid.NewGuid(), UserId = userId, AccountId = destination, Amount = template.Amount, Type = TransactionType.TransferIn, OccurredOnUtc = next, Note = template.Note, TransferGroupId = group }, ct);
                    template.LastGeneratedOnUtc = next;
                    generated++;
                    catchUpCount++;
                    next = template.Interval == RecurringInterval.Weekly
                        ? next.AddDays(7)
                        : GetNextAnchoredMonthlyOccurrence(template.StartOnUtc, next);
                }
            }
            await _repository.SaveChangesAsync(ct);
            return generated;
        }, ct);
    }

    private async Task<RecurringTransactionTemplate> GetRequiredTemplateAsync(Guid userId, Guid templateId, CancellationToken ct)
    {
        var template = await _repository.GetByIdAsync(userId, templateId, ct);
        return template ?? throw new KeyNotFoundException("Recurring template not found");
    }

    private async Task<(TransactionType Type, RecurringInterval Interval, DateTime StartOnUtc)> ValidateTemplateFieldsAsync(Guid userId, Guid accountId, Guid? categoryId, decimal amount, string type, string interval, DateTime startOnUtc, CancellationToken ct, Guid? destinationAccountId = null)
    {
        var normalizedStart = startOnUtc.Kind == DateTimeKind.Utc
            ? startOnUtc
            : startOnUtc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(startOnUtc, DateTimeKind.Utc)
                : startOnUtc.ToUniversalTime();

        if (amount <= 0) throw new InvalidOperationException("Amount must be greater than zero");
        var isTransfer = type.Equals("Transfer", StringComparison.OrdinalIgnoreCase);
        var txType = TransactionType.TransferOut;
        if (!isTransfer && (!Enum.TryParse<TransactionType>(type, true, out txType) || txType is not (TransactionType.Expense or TransactionType.Income or TransactionType.Refund))) throw new InvalidOperationException("Type must be Income, Expense, Refund, or Transfer");
        if (isTransfer && (!destinationAccountId.HasValue || destinationAccountId == accountId || !await _repository.AccountExistsAsync(userId, destinationAccountId.Value, ct))) throw new InvalidOperationException("Choose a different destination account owned by this user.");
        if (!Enum.TryParse<RecurringInterval>(interval, true, out var parsedInterval)) throw new InvalidOperationException("Interval must be Weekly or Monthly");

        if (!await _repository.AccountExistsAsync(userId, accountId, ct))
            throw new InvalidOperationException("Account does not belong to user");

        if (categoryId.HasValue && !await _repository.CategoryExistsAsync(userId, categoryId.Value, ct))
            throw new InvalidOperationException("Category does not belong to user");

        return (txType, parsedInterval, normalizedStart);
    }

    private static DateTime GetNextAnchoredMonthlyOccurrence(DateTime startOnUtc, DateTime previousOccurrenceUtc)
    {
        // Compute next month/year from previous occurrence
        var year = previousOccurrenceUtc.Year;
        var month = previousOccurrenceUtc.Month + 1;
        if (month > 12)
        {
            month = 1;
            year++;
        }

        // Anchor to the original day from startOnUtc, clamped to valid days in target month
        var day = Math.Min(startOnUtc.Day, DateTime.DaysInMonth(year, month));

        // Preserve the time components from startOnUtc
        return new DateTime(year, month, day, startOnUtc.Hour, startOnUtc.Minute, startOnUtc.Second, DateTimeKind.Utc);
    }
}
