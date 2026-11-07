using System.ComponentModel.DataAnnotations;

namespace Ledgerra.Api.Contracts;

public sealed class CreateCategoryRequest
{
    [Required, MaxLength(120)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public string Kind { get; init; } = string.Empty;

    public string? Color { get; init; }
}

public sealed class UpdateCategoryRequest
{
    [Required, MaxLength(120)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public string Kind { get; init; } = string.Empty;

    public string? Color { get; init; }

    public bool IsSystem { get; init; }
}

public sealed record CategoryResponse(Guid Id, string Name, string Kind, string? Color, bool IsSystem, Guid? ParentCategoryId = null);
