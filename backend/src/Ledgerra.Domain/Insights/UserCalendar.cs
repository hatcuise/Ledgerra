namespace Ledgerra.Domain.Insights;
public static class UserCalendar
{
    public static DateTime UtcStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return zone.IsAmbiguousTime(local) ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime : TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
