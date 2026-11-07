using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Application.Transactions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[ReconciliationGuard]
[Route("api/transactions")]
public sealed class TransactionsController : ControllerBase
{
    private readonly CreateTransactionCommandHandler _createTransactionCommandHandler;
    private readonly GetTransactionByIdQueryHandler _getTransactionByIdQueryHandler;
    private readonly GetTransactionsQueryHandler _getTransactionsQueryHandler;
    private readonly UpdateTransactionCommandHandler _updateTransactionCommandHandler;
    private readonly DeleteTransactionCommandHandler _deleteTransactionCommandHandler;
    private readonly MoveTransactionAccountCommandHandler _moveTransactionAccountCommandHandler;

    public TransactionsController(
        CreateTransactionCommandHandler createTransactionCommandHandler,
        GetTransactionByIdQueryHandler getTransactionByIdQueryHandler,
        GetTransactionsQueryHandler getTransactionsQueryHandler,
        UpdateTransactionCommandHandler updateTransactionCommandHandler,
        DeleteTransactionCommandHandler deleteTransactionCommandHandler,
        MoveTransactionAccountCommandHandler moveTransactionAccountCommandHandler)
    {
        _createTransactionCommandHandler = createTransactionCommandHandler;
        _getTransactionByIdQueryHandler = getTransactionByIdQueryHandler;
        _getTransactionsQueryHandler = getTransactionsQueryHandler;
        _updateTransactionCommandHandler = updateTransactionCommandHandler;
        _deleteTransactionCommandHandler = deleteTransactionCommandHandler;
        _moveTransactionAccountCommandHandler = moveTransactionAccountCommandHandler;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TransactionResponse>>> GetAll(
        [FromQuery] Guid[] accountId,
        [FromQuery] Guid[] categoryId,
        string? type,
        DateOnly? from,
        DateOnly? to,
        decimal? minAmount,
        decimal? maxAmount,
        string? q,
        bool uncategorizedOnly,
        CancellationToken cancellationToken)
    {
        var transactions = await _getTransactionsQueryHandler.HandleAsync(
            new GetTransactionsQuery(
                User.GetRequiredUserId(),
                accountId,
                categoryId,
                type,
                from,
                to,
                minAmount,
                maxAmount,
                q,
                uncategorizedOnly),
            cancellationToken);

        return Ok(transactions.Select(MapTransaction));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TransactionResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var transaction = await _getTransactionByIdQueryHandler.HandleAsync(
            new GetTransactionByIdQuery(User.GetRequiredUserId(), id),
            cancellationToken);

        return transaction is null ? NotFound() : Ok(MapTransaction(transaction));
    }

    [HttpPost]
    public async Task<ActionResult<TransactionResponse>> Create(CreateTransactionRequest request, CancellationToken cancellationToken)
    {
        var result = await _createTransactionCommandHandler.HandleAsync(
            new CreateTransactionCommand(
                User.GetRequiredUserId(),
                request.AccountId,
                request.CategoryId,
                request.DestinationAccountId,
                request.Amount,
                request.Type,
                request.OccurredOnUtc,
                request.Note,
                request.SavingsGoalId,
                request.SplitLines?.Select(line => new TransactionSplitLine(line.CategoryId, line.Amount)).ToList()),
            cancellationToken);

        if (result.IsNotFound)
        {
            return NotFound(new ProblemDetails
            {
                Title = result.NotFoundTitle
            });
        }

        if (result.HasValidationError)
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                [result.ValidationKey!] = [result.ValidationMessage!]
            });
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Transaction!.Id }, MapTransaction(result.Transaction));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TransactionResponse>> Update(Guid id, UpdateTransactionRequest request, CancellationToken cancellationToken)
    {
        var result = await _updateTransactionCommandHandler.HandleAsync(
            new UpdateTransactionCommand(
                User.GetRequiredUserId(),
                id,
                request.CategoryId,
                request.DestinationAccountId,
                request.Amount,
                request.Type,
                request.OccurredOnUtc,
                request.Note,
                request.SavingsGoalId,
                request.SplitLines?.Select(line => new TransactionSplitLine(line.CategoryId, line.Amount)).ToList()),
            cancellationToken);

        if (result.IsNotFound)
        {
            return NotFound(new ProblemDetails
            {
                Title = result.NotFoundTitle
            });
        }

        if (result.HasValidationError)
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                [result.ValidationKey!] = [result.ValidationMessage!]
            });
        }

        return Ok(MapTransaction(result.Transaction!));
    }

    [HttpPost("{id:guid}/move-account")]
    public async Task<ActionResult<TransactionResponse>> MoveAccount(
        Guid id,
        MoveTransactionAccountRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _moveTransactionAccountCommandHandler.HandleAsync(
            new MoveTransactionAccountCommand(User.GetRequiredUserId(), id, request.DestinationAccountId),
            cancellationToken);

        if (result.IsNotFound)
        {
            return NotFound(new ProblemDetails
            {
                Title = result.NotFoundTitle
            });
        }

        if (result.HasValidationError)
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                [result.ValidationKey!] = [result.ValidationMessage!]
            });
        }

        return Ok(MapTransaction(result.Transaction!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _deleteTransactionCommandHandler.HandleAsync(
            new DeleteTransactionCommand(User.GetRequiredUserId(), id),
            cancellationToken);

        if (!result.WasDeleted)
        {
            return NotFound();
        }

        return NoContent();
    }

    private static TransactionResponse MapTransaction(TransactionDetails transaction)
    {
        return new TransactionResponse(
            transaction.Id,
            transaction.AccountId,
            transaction.CategoryId,
            transaction.Amount,
            transaction.Type,
            transaction.OccurredOnUtc,
            transaction.Note,
            transaction.TransferGroupId,
            transaction.SavingsGoalId,
            transaction.SplitGroupId,
            transaction.ParentTransactionId);
    }
}
