using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Application.Categories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/categories")]
public sealed class CategoriesController : ControllerBase
{
    private readonly CreateCategoryCommandHandler _createCategoryCommandHandler;
    private readonly DeleteCategoryCommandHandler _deleteCategoryCommandHandler;
    private readonly GetCategoriesQueryHandler _getCategoriesQueryHandler;
    private readonly GetCategoryByIdQueryHandler _getCategoryByIdQueryHandler;
    private readonly UpdateCategoryCommandHandler _updateCategoryCommandHandler;

    public CategoriesController(
        CreateCategoryCommandHandler createCategoryCommandHandler,
        DeleteCategoryCommandHandler deleteCategoryCommandHandler,
        GetCategoriesQueryHandler getCategoriesQueryHandler,
        GetCategoryByIdQueryHandler getCategoryByIdQueryHandler,
        UpdateCategoryCommandHandler updateCategoryCommandHandler)
    {
        _createCategoryCommandHandler = createCategoryCommandHandler;
        _deleteCategoryCommandHandler = deleteCategoryCommandHandler;
        _getCategoriesQueryHandler = getCategoriesQueryHandler;
        _getCategoryByIdQueryHandler = getCategoryByIdQueryHandler;
        _updateCategoryCommandHandler = updateCategoryCommandHandler;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var categories = await _getCategoriesQueryHandler.HandleAsync(new GetCategoriesQuery(User.GetRequiredUserId()), cancellationToken);
        return Ok(categories.Select(MapCategory));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var category = await _getCategoryByIdQueryHandler.HandleAsync(new GetCategoryByIdQuery(User.GetRequiredUserId(), id), cancellationToken);

        return category is null ? NotFound() : Ok(MapCategory(category));
    }

    [HttpPost]
    public async Task<ActionResult<CategoryResponse>> Create(CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await _createCategoryCommandHandler.HandleAsync(
            new CreateCategoryCommand(User.GetRequiredUserId(), request.Name, request.Kind, request.Color),
            cancellationToken);

        if (result.HasValidationError)
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                [result.ValidationKey!] = [result.ValidationMessage!]
            });
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Category!.Id }, MapCategory(result.Category));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> Update(Guid id, UpdateCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await _updateCategoryCommandHandler.HandleAsync(
            new UpdateCategoryCommand(User.GetRequiredUserId(), id, request.Name, request.Kind, request.Color, request.IsSystem),
            cancellationToken);

        if (result.HasValidationError)
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                [result.ValidationKey!] = [result.ValidationMessage!]
            });
        }

        if (result.IsNotFound)
        {
            return NotFound();
        }

        return Ok(MapCategory(result.Category!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _deleteCategoryCommandHandler.HandleAsync(
            new DeleteCategoryCommand(User.GetRequiredUserId(), id),
            cancellationToken);

        if (result == CategoryDeleteStatus.NotFound)
        {
            return NotFound();
        }

        if (result == CategoryDeleteStatus.UsedByDependencies)
            return Conflict(new ProblemDetails { Title = "Category is used by other records. Merge it into another category instead." });

        if (result == CategoryDeleteStatus.UsedByImportRules)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Category is used by import rules",
                Detail = "Delete or move those import rules before deleting this category."
            });
        }

        return NoContent();
    }

    private static CategoryResponse MapCategory(CategoryDetails category)
    {
        return new CategoryResponse(category.Id, category.Name, category.Kind, category.Color, category.IsSystem, category.ParentCategoryId);
    }
}
