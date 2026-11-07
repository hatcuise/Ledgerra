using Ledgerra.Domain.Categories;

namespace Ledgerra.Application.Categories;

public sealed record GetCategoriesQuery(Guid UserId);

public sealed record GetCategoryByIdQuery(Guid UserId, Guid CategoryId);

public sealed record CreateCategoryCommand(Guid UserId, string Name, string Kind, string? Color);

public sealed record UpdateCategoryCommand(Guid UserId, Guid CategoryId, string Name, string Kind, string? Color, bool IsSystem);

public sealed record DeleteCategoryCommand(Guid UserId, Guid CategoryId);

public sealed record CategoryDetails(Guid Id, string Name, string Kind, string? Color, bool IsSystem, Guid? ParentCategoryId = null);

public enum CategoryDeleteStatus
{
    Deleted,
    NotFound,
    UsedByImportRules,
    UsedByDependencies
}

public interface ICategoryStore
{
    Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken);

    Task<Category?> GetByIdAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken);

    Task<Category> CreateAsync(Category category, CancellationToken cancellationToken);

    Task<Category?> UpdateAsync(
        Guid userId,
        Guid categoryId,
        string name,
        CategoryKind kind,
        string? color,
        bool isSystem,
        CancellationToken cancellationToken);

    Task<CategoryDeleteStatus> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken);
}

public sealed class GetCategoriesQueryHandler
{
    private readonly ICategoryStore _categoryStore;

    public GetCategoriesQueryHandler(ICategoryStore categoryStore)
    {
        _categoryStore = categoryStore;
    }

    public async Task<IReadOnlyList<CategoryDetails>> HandleAsync(GetCategoriesQuery query, CancellationToken cancellationToken)
    {
        var categories = await _categoryStore.GetAllAsync(query.UserId, cancellationToken);
        return categories.Select(CategoryMappings.MapCategory).ToList();
    }
}

public sealed class GetCategoryByIdQueryHandler
{
    private readonly ICategoryStore _categoryStore;

    public GetCategoryByIdQueryHandler(ICategoryStore categoryStore)
    {
        _categoryStore = categoryStore;
    }

    public async Task<CategoryDetails?> HandleAsync(GetCategoryByIdQuery query, CancellationToken cancellationToken)
    {
        var category = await _categoryStore.GetByIdAsync(query.UserId, query.CategoryId, cancellationToken);
        return category is null ? null : CategoryMappings.MapCategory(category);
    }
}

public sealed class CreateCategoryCommandHandler
{
    private readonly ICategoryStore _categoryStore;

    public CreateCategoryCommandHandler(ICategoryStore categoryStore)
    {
        _categoryStore = categoryStore;
    }

    public async Task<CategoryCommandResult> HandleAsync(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CategoryKind>(command.Kind, true, out var kind))
        {
            return CategoryCommandResult.ValidationError("kind", "Unsupported category kind.");
        }

        var category = await _categoryStore.CreateAsync(
            new Category
            {
                Id = Guid.NewGuid(),
                UserId = command.UserId,
                Name = command.Name.Trim(),
                Kind = kind,
                Color = command.Color,
                IsSystem = false
            },
            cancellationToken);

        return CategoryCommandResult.Success(CategoryMappings.MapCategory(category));
    }
}

public sealed class UpdateCategoryCommandHandler
{
    private readonly ICategoryStore _categoryStore;

    public UpdateCategoryCommandHandler(ICategoryStore categoryStore)
    {
        _categoryStore = categoryStore;
    }

    public async Task<CategoryCommandResult> HandleAsync(UpdateCategoryCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CategoryKind>(command.Kind, true, out var kind))
        {
            return CategoryCommandResult.ValidationError("kind", "Unsupported category kind.");
        }

        var all = await _categoryStore.GetAllAsync(command.UserId, cancellationToken);
        var existing = all.SingleOrDefault(x => x.Id == command.CategoryId);
        if (existing is not null && existing.Kind != kind && (existing.ParentCategoryId.HasValue || all.Any(x => x.ParentCategoryId == existing.Id)))
            return CategoryCommandResult.ValidationError("kind", "Detach category hierarchy before changing its kind.");

        var category = await _categoryStore.UpdateAsync(
            command.UserId,
            command.CategoryId,
            command.Name.Trim(),
            kind,
            command.Color,
            command.IsSystem,
            cancellationToken);

        return category is null
            ? CategoryCommandResult.NotFound()
            : CategoryCommandResult.Success(CategoryMappings.MapCategory(category));
    }
}

public sealed class DeleteCategoryCommandHandler
{
    private readonly ICategoryStore _categoryStore;

    public DeleteCategoryCommandHandler(ICategoryStore categoryStore)
    {
        _categoryStore = categoryStore;
    }

    public Task<CategoryDeleteStatus> HandleAsync(DeleteCategoryCommand command, CancellationToken cancellationToken)
    {
        return _categoryStore.DeleteAsync(command.UserId, command.CategoryId, cancellationToken);
    }
}

public sealed class CategoryCommandResult
{
    private CategoryCommandResult(CategoryDetails? category, string? validationKey, string? validationMessage, bool notFound)
    {
        Category = category;
        ValidationKey = validationKey;
        ValidationMessage = validationMessage;
        IsNotFound = notFound;
    }

    public CategoryDetails? Category { get; }

    public string? ValidationKey { get; }

    public string? ValidationMessage { get; }

    public bool HasValidationError => ValidationKey is not null;

    public bool IsNotFound { get; }

    public static CategoryCommandResult Success(CategoryDetails category) => new(category, null, null, false);

    public static CategoryCommandResult ValidationError(string key, string message) => new(null, key, message, false);

    public static CategoryCommandResult NotFound() => new(null, null, null, true);
}

internal static class CategoryMappings
{
    public static CategoryDetails MapCategory(Category category)
    {
        return new CategoryDetails(category.Id, category.Name, category.Kind.ToString(), category.Color, category.IsSystem, category.ParentCategoryId);
    }
}