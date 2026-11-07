namespace Ledgerra.Domain.Categories;

public sealed class Category
{
    public Guid Id { get; set; }

    public Guid? ParentCategoryId { get; set; }

    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public CategoryKind Kind { get; set; } = CategoryKind.Expense;

    public string? Color { get; set; }

    public bool IsSystem { get; set; }

    public List<Transactions.Transaction> Transactions { get; set; } = [];
}
