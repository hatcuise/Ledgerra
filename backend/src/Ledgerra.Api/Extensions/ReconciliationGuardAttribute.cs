using System.Data;
using Ledgerra.Api.Contracts;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Extensions;

public sealed class ReconciliationGuardAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method) || !context.ActionArguments.TryGetValue("id", out var raw) || raw is not Guid id) { await next(); return; }
        var db = context.HttpContext.RequestServices.GetRequiredService<LedgerraDbContext>();
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, context.HttpContext.RequestAborted) : null;
        var userId = context.HttpContext.User.GetRequiredUserId();
        var ct = context.HttpContext.RequestAborted;
        var locked = false;
        if (context.Controller is Controllers.TransactionsController)
        {
            var entry = await db.Transactions.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == id, ct);
            if (entry is not null)
            {
                var related = db.Transactions.Where(x => x.UserId == userId && (x.Id == id ||
                    (entry.TransferGroupId != null && x.TransferGroupId == entry.TransferGroupId) ||
                    (entry.SplitGroupId != null && x.SplitGroupId == entry.SplitGroupId))).Select(x => x.Id);
                locked = await db.EntryMetadata.AnyAsync(x => x.UserId == userId && related.Contains(x.Id) && x.Status == "Reconciled", ct);
            }
        }
        else
        {
            var hasStatement = await db.Reconciliations.AnyAsync(x => x.UserId == userId && x.AccountId == id, ct);
            if (hasStatement)
            {
                if (HttpMethods.IsDelete(context.HttpContext.Request.Method)) locked = true;
                if (context.ActionArguments.TryGetValue("request", out var body) && body is UpdateAccountRequest update)
                {
                    var account = await db.Accounts.SingleAsync(x => x.UserId == userId && x.Id == id, ct);
                    locked = account.OpeningBalance != update.OpeningBalance || account.CurrencyCode != update.CurrencyCode;
                }
            }
        }
        if (locked) { context.Result = new ConflictObjectResult(new ProblemDetails { Title = "Reopen the reconciled statement before changing its transactions or opening balance." }); return; }
        var executed = await next();
        if (tx is not null && executed.Exception is null) await tx.CommitAsync(ct);
    }
}
