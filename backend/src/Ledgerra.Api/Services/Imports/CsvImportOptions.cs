using System.Globalization;
using System.ComponentModel.DataAnnotations;

namespace Ledgerra.Api.Services.Imports;

public sealed record CsvImportOptions
{
    [Required]
    public string Delimiter { get; init; } = ",";
    [Required]
    public string CultureName { get; init; } = "en-US";
    public string? DateFormat { get; init; }
    public string? DebitColumn { get; init; }
    public string? CreditColumn { get; init; }
    public string? TypeColumn { get; init; }
    public string? PayeeColumn { get; init; }
    public string? CategoryColumn { get; init; }
    public string? ReferenceColumn { get; init; }
    public string? TagsColumn { get; init; }
    [Required]
    public string PositiveType { get; init; } = "Income";
    [Required]
    public string NegativeType { get; init; } = "Expense";
    public static string NormalizeType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "income" => "Income", "expense" => "Expense", "refund" => "Refund",
        _ => throw new InvalidOperationException($"Unsupported CSV transaction type '{value}'. Choose Income, Expense, or Refund. Use linked transfers for account payments.")
    };
    public void Validate()
    {
        if (Delimiter is not ("," or ";" or "\t" or "|")) throw new InvalidOperationException("Choose comma, semicolon, tab, or pipe as the delimiter.");
        try { _ = CultureInfo.GetCultureInfo(CultureName); }
        catch (CultureNotFoundException) { throw new InvalidOperationException("Unknown number/date locale."); }
        try { if (!string.IsNullOrWhiteSpace(DateFormat)) _ = DateTime.UtcNow.ToString(DateFormat, CultureInfo.InvariantCulture); }
        catch (FormatException) { throw new InvalidOperationException("Invalid date format."); }
        _ = NormalizeType(PositiveType); _ = NormalizeType(NegativeType);
        if (DateFormat?.Length > 80) throw new InvalidOperationException("Date format is too long.");
    }
}
