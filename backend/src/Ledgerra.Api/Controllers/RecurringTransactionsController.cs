using Ledgerra.Api.Contracts;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Ledgerra.Api.Extensions;
using Ledgerra.Application.Transactions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/recurring-transactions")]
public sealed class RecurringTransactionsController : ControllerBase
{
    private readonly RecurringTransactionUseCases _useCases;
    public RecurringTransactionsController(RecurringTransactionUseCases useCases) => _useCases = useCases;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RecurringTransactionTemplateResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _useCases.GetAllAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(items.Select(Map));
    }

    [HttpPost]
    public async Task<ActionResult<RecurringTransactionTemplateResponse>> Create(CreateRecurringTransactionTemplateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Type.Equals("Transfer", StringComparison.OrdinalIgnoreCase) && request.DestinationAccountId is Guid destination)
            {
                var db = HttpContext.RequestServices.GetRequiredService<LedgerraDbContext>();
                var accounts = await db.Accounts.Where(x => x.UserId == User.GetRequiredUserId() && (x.Id == request.AccountId || x.Id == destination)).ToListAsync(cancellationToken);
                if (accounts.Count != 2 || accounts.Select(x => x.CurrencyCode).Distinct().Count() != 1) return BadRequest(new { error = "Scheduled transfers need different accounts with the same currency." });
            }
            var item = await _useCases.CreateAsync(User.GetRequiredUserId(), request.AccountId, request.CategoryId, request.Amount, request.Type, request.Interval, request.StartOnUtc, request.Note, cancellationToken, request.DestinationAccountId);
            return Ok(Map(item));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{templateId:guid}")]
    public async Task<ActionResult<RecurringTransactionTemplateResponse>> Update(Guid templateId, UpdateRecurringTransactionTemplateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Type.Equals("Transfer", StringComparison.OrdinalIgnoreCase) && request.DestinationAccountId is Guid destination)
            {
                var db = HttpContext.RequestServices.GetRequiredService<LedgerraDbContext>();
                var accounts = await db.Accounts.Where(x => x.UserId == User.GetRequiredUserId() && (x.Id == request.AccountId || x.Id == destination)).ToListAsync(cancellationToken);
                if (accounts.Count != 2 || accounts.Select(x => x.CurrencyCode).Distinct().Count() != 1) return BadRequest(new { error = "Scheduled transfers need different accounts with the same currency." });
            }
            var item = await _useCases.UpdateAsync(User.GetRequiredUserId(), templateId, request.AccountId, request.CategoryId, request.Amount, request.Type, request.Interval, request.StartOnUtc, request.IsActive, request.Note, cancellationToken, request.DestinationAccountId);
            return Ok(Map(item));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPatch("{templateId:guid}/status")]
    public async Task<ActionResult<RecurringTransactionTemplateResponse>> UpdateStatus(Guid templateId, UpdateRecurringTransactionTemplateStatusRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var item = await _useCases.SetActiveAsync(User.GetRequiredUserId(), templateId, request.IsActive, cancellationToken);
            return Ok(Map(item));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{templateId:guid}")]
    public async Task<IActionResult> Delete(Guid templateId, CancellationToken cancellationToken)
    {
        try
        {
            await _useCases.DeleteAsync(User.GetRequiredUserId(), templateId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("generate")]
    public async Task<ActionResult<object>> Generate(CancellationToken cancellationToken)
    {
        var count = await _useCases.GenerateDueAsync(User.GetRequiredUserId(), DateTime.UtcNow, cancellationToken);
        return Ok(new { generated = count });
    }

    private static RecurringTransactionTemplateResponse Map(Ledgerra.Domain.Transactions.RecurringTransactionTemplate item)
        => new(item.Id, item.AccountId, item.CategoryId, item.Amount, item.DestinationAccountId.HasValue ? "Transfer" : item.Type.ToString(), item.Interval.ToString(), item.StartOnUtc, item.LastGeneratedOnUtc, item.IsActive, item.Note, item.DestinationAccountId);
}
