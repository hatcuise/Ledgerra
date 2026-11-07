using Ledgerra.Application.Categories;
using Ledgerra.Domain.Categories;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public sealed class CategoryStore : ICategoryStore
{
    private readonly LedgerraDbContext _dbContext;

    public CategoryStore(LedgerraDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Categories
            .Where(category => category.UserId == userId)
            .OrderBy(category => category.Kind)
            .ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<Category?> GetByIdAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        return _dbContext.Categories.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == categoryId,
            cancellationToken);
    }

    public async Task<Category> CreateAsync(Category category, CancellationToken cancellationToken)
    {
        _dbContext.Categories.Add(category);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return category;
    }

    public async Task<Category?> UpdateAsync(
        Guid userId,
        Guid categoryId,
        string name,
        CategoryKind kind,
        string? color,
        bool isSystem,
        CancellationToken cancellationToken)
    {
        var category = await _dbContext.Categories.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == categoryId,
            cancellationToken);

        if (category is null)
        {
            return null;
        }

        category.Name = name;
        category.Kind = kind;
        category.Color = color;
        category.IsSystem = isSystem;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return category;
    }

    public async Task<CategoryDeleteStatus> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await _dbContext.Categories.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == categoryId,
            cancellationToken);

        if (category is null)
        {
            return CategoryDeleteStatus.NotFound;
        }

        var isUsedByImportRules = await _dbContext.CategorizationRules.AnyAsync(
            rule => rule.UserId == userId && rule.AssignCategoryId == categoryId,
            cancellationToken);

        if (isUsedByImportRules)
        {
            return CategoryDeleteStatus.UsedByImportRules;
        }

        if (await _dbContext.Categories.AnyAsync(x => x.UserId == userId && x.ParentCategoryId == categoryId, cancellationToken)
            || await _dbContext.Payees.AnyAsync(x => x.UserId == userId && x.DefaultCategoryId == categoryId, cancellationToken)
            || await _dbContext.RecurringTransactionTemplates.AnyAsync(x => x.UserId == userId && x.CategoryId == categoryId, cancellationToken)
            || await _dbContext.BudgetCategoryLimits.AnyAsync(x => x.CategoryId == categoryId, cancellationToken)
            || await _dbContext.TransferMatches.AnyAsync(x => x.UserId == userId && (x.OutgoingCategoryId == categoryId || x.IncomingCategoryId == categoryId), cancellationToken))
            return CategoryDeleteStatus.UsedByDependencies;
        _dbContext.Categories.Remove(category);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsImportRuleCategoryReferenceViolation(exception))
        {
            return CategoryDeleteStatus.UsedByImportRules;
        }

        return CategoryDeleteStatus.Deleted;
    }

    private static bool IsImportRuleCategoryReferenceViolation(DbUpdateException exception)
    {
        var innerException = exception.InnerException;
        if (innerException?.GetType().FullName != "Npgsql.PostgresException")
        {
            return false;
        }

        var sqlState = innerException.GetType().GetProperty("SqlState")?.GetValue(innerException) as string;
        var constraintName = innerException.GetType().GetProperty("ConstraintName")?.GetValue(innerException) as string;
        return sqlState == "23503" && constraintName == "FK_CategorizationRules_Categories_AssignCategoryId";
    }
}