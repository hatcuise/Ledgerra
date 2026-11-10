using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Ledgerra.Api.Extensions;
using Ledgerra.Api.Services.Insights;
using Ledgerra.Domain.Insights;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Ledgerra.Api.Controllers;

[ApiController, Authorize, Route("api/insights")]
public sealed class InsightsController(LedgerraDbContext db, SpendingInsightsService insights, TimeProvider clock) : ControllerBase
{
    private Guid UserId => User.GetRequiredUserId();
    private bool Parse(string? month, DateOnly? today, string? timeZone, out DateOnly parsed, out DateOnly asOf, out TimeZoneInfo zone)
    {
        parsed = default; asOf = default; zone = TimeZoneInfo.Utc;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "UTC"); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
        var serverDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime); asOf = today ?? serverDate;
        return DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            && parsed.Year is >= 1900 and <= 9998 && Math.Abs(asOf.DayNumber - serverDate.DayNumber) <= 1;
    }
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string month, [FromQuery] DateOnly? today, [FromQuery] string? timeZone, CancellationToken ct)
    {
        if (!Parse(month, today, timeZone, out var parsed, out var asOf, out var zone)) return BadRequest(new ProblemDetails { Title = "Use a valid YYYY-MM month and the current browser calendar date." });
        return Ok(await insights.BuildAsync(UserId, parsed, asOf, ct, zone));
    }
    [HttpGet("preferences")]
    public async Task<IActionResult> Preferences(CancellationToken ct) => Ok(await db.InsightPreferences.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == UserId, ct) ?? new InsightPreferences { UserId = UserId });
    [HttpPut("preferences")]
    public async Task<IActionResult> SavePreferences(InsightPreferences request, CancellationToken ct)
    {
        if (!request.IsValid()) return BadRequest(new ProblemDetails { Title = "Use valid positive thresholds with at most two decimal places; budget warning 1–100%, spending increase 1–1000%, unusual multiplier 1.5–100." });
        request.UserId = UserId;
        var existing = await db.InsightPreferences.SingleOrDefaultAsync(x => x.UserId == UserId, ct);
        if (existing is null) db.InsightPreferences.Add(request); else db.Entry(existing).CurrentValues.SetValues(request);
        await db.SaveChangesAsync(ct); return Ok(request);
    }
    [HttpPost("dismiss")]
    public async Task<IActionResult> Dismiss(DismissInsightRequest request, CancellationToken ct)
    {
        if (!Parse(request.Month, request.Today, request.TimeZone, out var parsed, out var asOf, out var zone)) return BadRequest();
        var result = await insights.BuildAsync(UserId, parsed, asOf, ct, zone);
        if (!result.Alerts.Any(x => x.Key == request.Key)) return NotFound();
        if (!await db.InsightDismissals.AnyAsync(x => x.UserId == UserId && x.AlertKey == request.Key, ct))
        {
            db.InsightDismissals.Add(new InsightDismissal { Id = Guid.NewGuid(), UserId = UserId, AlertKey = request.Key, Month = request.Month, DismissedAtUtc = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
    [HttpDelete("dismiss/{key}")]
    public async Task<IActionResult> Restore(string key, CancellationToken ct)
    {
        var item = await db.InsightDismissals.SingleOrDefaultAsync(x => x.UserId == UserId && x.AlertKey == key, ct);
        if (item is null) return NotFound(); db.InsightDismissals.Remove(item); await db.SaveChangesAsync(ct); return NoContent();
    }
}
public sealed class DismissInsightRequest
{
    [Required, RegularExpression("^[0-9A-F]{64}$")] public string Key { get; init; } = "";
    [Required, MaxLength(7)] public string Month { get; init; } = "";
    public DateOnly? Today { get; init; }
    [MaxLength(120)] public string? TimeZone { get; init; }
}
