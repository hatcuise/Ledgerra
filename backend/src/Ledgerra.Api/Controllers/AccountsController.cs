using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Application.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[ReconciliationGuard]
[Route("api/accounts")]
public sealed class AccountsController : ControllerBase
{
    private readonly CreateAccountCommandHandler _createAccountCommandHandler;
    private readonly DeleteAccountCommandHandler _deleteAccountCommandHandler;
    private readonly GetAccountByIdQueryHandler _getAccountByIdQueryHandler;
    private readonly GetAccountsQueryHandler _getAccountsQueryHandler;
    private readonly UpdateAccountCommandHandler _updateAccountCommandHandler;

    public AccountsController(
        CreateAccountCommandHandler createAccountCommandHandler,
        DeleteAccountCommandHandler deleteAccountCommandHandler,
        GetAccountByIdQueryHandler getAccountByIdQueryHandler,
        GetAccountsQueryHandler getAccountsQueryHandler,
        UpdateAccountCommandHandler updateAccountCommandHandler)
    {
        _createAccountCommandHandler = createAccountCommandHandler;
        _deleteAccountCommandHandler = deleteAccountCommandHandler;
        _getAccountByIdQueryHandler = getAccountByIdQueryHandler;
        _getAccountsQueryHandler = getAccountsQueryHandler;
        _updateAccountCommandHandler = updateAccountCommandHandler;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AccountResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var accounts = await _getAccountsQueryHandler.HandleAsync(
            new GetAccountsQuery(User.GetRequiredUserId()),
            cancellationToken);
        return Ok(accounts.Select(MapAccount));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AccountResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var account = await _getAccountByIdQueryHandler.HandleAsync(
            new GetAccountByIdQuery(User.GetRequiredUserId(), id),
            cancellationToken);

        return account is null ? NotFound() : Ok(MapAccount(account));
    }

    [HttpPost]
    public async Task<ActionResult<AccountResponse>> Create(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await _createAccountCommandHandler.HandleAsync(
            new CreateAccountCommand(
                User.GetRequiredUserId(),
                request.Name,
                request.Type,
                request.CurrencyCode,
                request.OpeningBalance,
                request.ExcludeFromBudget,
                request.ExcludeFromNetWorth,
                request.InstitutionName,
                request.AccountNumberMasked,
                request.IconKind),
            cancellationToken);

        if (result.HasValidationError)
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                [result.ValidationKey!] = [result.ValidationMessage!]
            });
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Account!.Id }, MapAccount(result.Account));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AccountResponse>> Update(Guid id, UpdateAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await _updateAccountCommandHandler.HandleAsync(
            new UpdateAccountCommand(
                User.GetRequiredUserId(),
                id,
                request.Name,
                request.Type,
                request.CurrencyCode,
                request.OpeningBalance,
                request.IsActive,
                request.ExcludeFromBudget,
                request.ExcludeFromNetWorth,
                request.InstitutionName,
                request.AccountNumberMasked,
                request.IconKind),
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

        return Ok(MapAccount(result.Account!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _deleteAccountCommandHandler.HandleAsync(
            new DeleteAccountCommand(User.GetRequiredUserId(), id),
            cancellationToken);

        if (result == AccountDeleteStatus.NotFound)
        {
            return NotFound();
        }

        if (result == AccountDeleteStatus.HasTransactions)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Account has transactions",
                Detail = "Delete or move transactions before removing the account."
            });
        }

        return NoContent();
    }

    private static AccountResponse MapAccount(AccountDetails account)
    {
        return new AccountResponse(
            account.Id,
            account.Name,
            account.Type,
            account.CurrencyCode,
            account.OpeningBalance,
            account.CurrentBalance,
            account.IsActive,
            account.ExcludeFromBudget,
            account.ExcludeFromNetWorth,
            account.InstitutionName,
            account.AccountNumberMasked,
            account.IconKind);
    }
}
