using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Api.Services.Imports;
using Ledgerra.Domain.Categories;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController, Authorize, Route("api/imports/native")]
public sealed class NativeImportsController(NativeStatementReader reader, LedgerraDbContext db,
    IImportCategorizationRuleMatcher rules, IImportDuplicateDetector duplicates) : ControllerBase
{
    [HttpPost("inspect")]
    public async Task<IActionResult> Inspect([FromForm] NativeInspectRequest request, CancellationToken ct)
    {
        try
        {
            var statement = await reader.ReadAsync(request.File, request.Encoding, request.DateOrder, ct);
            return Ok(new { statement.Format, Accounts = statement.Accounts.Select(a => new { a.Key, a.Name, a.Currency, a.OpeningBalance, TransactionCount = statement.Entries.Count(e => e.AccountKey == a.Key) }), statement.Warnings });
        }
        catch (InvalidOperationException e) { return BadRequest(new ProblemDetails { Title = e.Message }); }
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromForm] NativePreviewRequest request, CancellationToken ct)
    {
        try
        {
            var statement = await reader.ReadAsync(request.File, request.Encoding, request.DateOrder, ct);
            var mappings = JsonSerializer.Deserialize<Dictionary<string, Guid?>>(request.AccountMappings)
                ?? throw new InvalidOperationException("Map every source account to an existing account or explicitly skip it.");
            if (mappings.Keys.Any(k => !statement.Accounts.Any(a => a.Key == k))) return BadRequest(new ProblemDetails { Title = "The account mapping does not match this statement." });
            var destinations = mappings.Values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (destinations.Distinct().Count() != destinations.Count) throw new InvalidOperationException("Map source accounts to distinct destination accounts so internal transfers remain distinguishable.");
            var userId = User.GetRequiredUserId();
            var accounts = await db.Accounts.Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id, ct);
            foreach (var source in statement.Accounts)
            {
                if (!mappings.TryGetValue(source.Key, out var target)) throw new InvalidOperationException("Map every source account or choose Skip.");
                if (target is null) continue;
                if (!accounts.TryGetValue(target.Value, out var account)) return NotFound(new ProblemDetails { Title = "Destination account not found." });
                if (source.Currency is not null && !source.Currency.Equals(account.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Account '{source.Name}' uses {source.Currency}; choose a destination with the same currency.");
            }
            var categories = await db.Categories.Where(c => c.UserId == userId).ToListAsync(ct);
            var raw = statement.Entries.Where(e => mappings[e.AccountKey].HasValue).ToList();
            if (raw.Count == 0) throw new InvalidOperationException("Select at least one source account with transactions.");
            var initial = new List<ImportDraftReviewItem>();
            foreach (var row in raw)
            {
                NativeStatementReader.ValidateEntry(row);
                var rowWarnings = row.Warnings.ToList();
                var type = row.SignedAmount > 0 ? "Income" : "Expense";
                var categoryMatches = categories.Where(c => row.Category is not null && c.Name.Equals(row.Category, StringComparison.OrdinalIgnoreCase)).ToList();
                Guid? categoryId = null;
                if (categoryMatches.Count == 1)
                {
                    var category = categoryMatches[0]; categoryId = category.Id;
                    if (row.SignedAmount > 0 && category.Kind == CategoryKind.Expense && !(row.Memo ?? "").StartsWith("Transfer", StringComparison.OrdinalIgnoreCase)) type = "Refund";
                }
                else if (row.Category is not null) rowWarnings.Add($"Category '{row.Category}' was not uniquely matched. Choose a Ledgerra category.");
                var memo = row.Memo ?? row.Payee;
                if (row.CounterpartyAccountKey is not null && mappings.TryGetValue(row.CounterpartyAccountKey, out var otherId) && otherId.HasValue && accounts.TryGetValue(otherId.Value, out var other))
                    memo = $"Transfer {(row.SignedAmount < 0 ? "to" : "from")} {other.Name}: {memo}";
                if (memo?.Length > 400) throw new InvalidOperationException("A transfer description exceeds the supported length after account mapping. Shorten its source memo before importing.");
                initial.Add(new ImportDraftReviewItem(row.SourceId, mappings[row.AccountKey]!.Value, categoryId,
                    Math.Abs(row.SignedAmount), type, row.Date, memo, 1, rowWarnings,
                    null, null, false, null, null, rowWarnings.Count == 0));
            }
            var reviewed = await duplicates.MarkDuplicatesAsync(userId, await rules.ApplyAsync(userId, initial, ct), ct);
            var identities = raw.Where(r => r.ExternalKey is not null).Select(r => r.ExternalKey!).ToArray();
            var existing = (await db.EntryMetadata.Where(m => m.UserId == userId && m.ExternalKey != null && identities.Contains(m.ExternalKey)).Select(m => m.ExternalKey!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rawById = raw.ToDictionary(r => r.SourceId);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var response = reviewed.Select(d =>
            {
                var row = rawById[d.SourceId]; var exact = row.ExternalKey is not null && (!seen.Add(row.ExternalKey) || existing.Contains(row.ExternalKey));
                return new MonthlyReportDraftTransactionResponse(d.SourceId, d.AccountId, d.CategoryId, d.Amount, d.Type,
                    d.OccurredOnUtc, d.Note, d.Confidence, d.Warnings, d.AppliedRuleId, d.AppliedRuleName,
                    exact || d.IsLikelyDuplicate, d.DuplicateTransactionId, exact ? "This bank FITID was already imported or repeated in the file." : d.DuplicateReason,
                    !exact && d.IsSelectedByDefault, row.Payee, row.Reference, row.Tags, row.ExternalKey);
            }).ToList();
            return Ok(new MonthlyReportAnalysisResponse(response, statement.Warnings));
        }
        catch (JsonException) { return BadRequest(new ProblemDetails { Title = "Invalid account mapping JSON." }); }
        catch (InvalidOperationException e) { return BadRequest(new ProblemDetails { Title = e.Message }); }
    }
}

public class NativeInspectRequest
{
    [Required] public IFormFile File { get; init; } = null!;
    [Required] public string Encoding { get; init; } = "utf-8";
    [Required] public string DateOrder { get; init; } = "mdy";
}
public sealed class NativePreviewRequest : NativeInspectRequest
{
    [Required, MaxLength(20000)] public string AccountMappings { get; init; } = "{}";
}
