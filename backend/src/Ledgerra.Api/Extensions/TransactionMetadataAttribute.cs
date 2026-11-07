using System.Data;
using Ledgerra.Api.Contracts;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Extensions;

// Encloses the money movement, split replacement, metadata and snapshots in one commit.
public sealed class TransactionMetadataAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.ActionArguments.TryGetValue("request", out var body) || body is not TransactionMetadataRequest request)
        { await next(); return; }
        var db = context.HttpContext.RequestServices.GetRequiredService<LedgerraDbContext>();
        var userId = context.HttpContext.User.GetRequiredUserId();
        var ct = context.HttpContext.RequestAborted;
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        if (request.UpdateMetadata && (request.Status is not ("Uncleared" or "Cleared")
            || request.Reference is null || request.Tags is null || request.PaymentMethod is null))
        { context.Result = new BadRequestObjectResult(new ProblemDetails { Title = "Invalid transaction metadata or status." }); return; }
        if (request.UpdateMetadata && request.PayeeId.HasValue && !await db.Payees.AnyAsync(x => x.UserId == userId && x.Id == request.PayeeId, ct))
        { context.Result = new NotFoundResult(); return; }
        var splitLines = body is CreateTransactionRequest create ? create.SplitLines : ((UpdateTransactionRequest)body).SplitLines;
        var amount = body is CreateTransactionRequest c ? c.Amount : ((UpdateTransactionRequest)body).Amount;
        if (amount != decimal.Round(amount, 2) || (splitLines is { Count: > 0 } && (splitLines.Count > 100 || splitLines.Count < 2 || splitLines.Any(x => x.Amount != decimal.Round(x.Amount, 2)))))
        { context.Result = new BadRequestObjectResult(new ProblemDetails { Title = "Use two decimal places and between two and 100 split allocations." }); return; }
        if (splitLines is { Count: > 0 })
        {
            var ids = splitLines.Select(x => x.CategoryId).Distinct().ToArray();
            if (await db.Categories.CountAsync(x => x.UserId == userId && ids.Contains(x.Id) && x.Kind == Ledgerra.Domain.Categories.CategoryKind.Expense, ct) != ids.Length)
            { context.Result = new BadRequestObjectResult(new ProblemDetails { Title = "Each allocation needs an owned expense category." }); return; }
        }
        var categoryId = body is CreateTransactionRequest cr ? cr.CategoryId : ((UpdateTransactionRequest)body).CategoryId;
        var type = body is CreateTransactionRequest crt ? crt.Type : ((UpdateTransactionRequest)body).Type;
        if (categoryId.HasValue && !(splitLines is { Count: > 0 }) && !type.Equals("Transfer", StringComparison.OrdinalIgnoreCase))
        {
            var category = await db.Categories.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == categoryId, ct);
            if (category is not null && category.Kind != (type.Equals("Income", StringComparison.OrdinalIgnoreCase) ? Ledgerra.Domain.Categories.CategoryKind.Income : Ledgerra.Domain.Categories.CategoryKind.Expense))
            { context.Result = new BadRequestObjectResult(new ProblemDetails { Title = "Category kind must match the transaction type." }); return; }
        }
        EntryMetadata? previous = null;
        if (context.ActionArguments.TryGetValue("id", out var raw) && raw is Guid oldId)
        {
            if (await db.Transactions.AnyAsync(x => x.UserId == userId && x.Id == oldId && x.ParentTransactionId != null, ct))
            { context.Result = new BadRequestObjectResult(new ProblemDetails { Title = "Edit split allocations through their parent transaction." }); return; }
            previous = await db.EntryMetadata.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.Id == oldId, ct);
        }
        var executed = await next();
        if (executed.Exception is not null || executed.Result is not ObjectResult { Value: TransactionResponse saved } result || result.StatusCode >= 400) return;
        if (request.UpdateMetadata || previous is not null)
        {
            var metadata = await db.EntryMetadata.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == saved.Id, ct);
            if (metadata is null) { metadata = new EntryMetadata { Id = saved.Id, UserId = userId }; db.EntryMetadata.Add(metadata); }
            // Preserve imported identities even when an ordinary entry is changed to a transfer.
            metadata.ExternalKey ??= previous?.ExternalKey;
            metadata.PayeeId = request.UpdateMetadata ? request.PayeeId : previous!.PayeeId;
            metadata.Reference = request.UpdateMetadata ? request.Reference.Trim() : previous!.Reference;
            metadata.Tags = request.UpdateMetadata ? request.Tags.Trim() : previous!.Tags;
            metadata.PaymentMethod = request.UpdateMetadata ? request.PaymentMethod.Trim() : previous!.PaymentMethod;
            metadata.Status = request.UpdateMetadata ? request.Status : previous!.Status;
            await db.SaveChangesAsync(ct);
        }
        if (tx is not null) await tx.CommitAsync(ct);
    }
}
