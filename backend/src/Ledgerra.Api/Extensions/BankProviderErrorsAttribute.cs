using System.Security.Cryptography;
using Ledgerra.Api.Services.Banking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
namespace Ledgerra.Api.Extensions;
public sealed class BankProviderErrorsAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is BankProviderException bank)
            context.Result = new ObjectResult(new ProblemDetails { Title = bank.Message, Extensions = { ["code"] = bank.Code } }) { StatusCode = 502 };
        else if (context.Exception is HttpRequestException or TaskCanceledException or CryptographicException)
            context.Result = new ObjectResult(new ProblemDetails { Title = "Unable to contact or unlock the bank connection. Check provider settings, connectivity and persisted data-protection keys, then retry." }) { StatusCode = 503 };
        else if (context.Exception is Microsoft.EntityFrameworkCore.DbUpdateException update
            && update.InnerException?.GetType().FullName == "Npgsql.PostgresException"
            && update.InnerException.GetType().GetProperty("SqlState")?.GetValue(update.InnerException) is string state && state is "23505" or "23503" or "40001")
            context.Result = new ConflictObjectResult(new ProblemDetails { Title = "Bank settings or mappings changed concurrently. Reload and retry." });
        else return;
        context.ExceptionHandled = true;
    }
}
