using System.Globalization;
using Ledgerra.Application.Imports;

namespace Ledgerra.Api.Services.Imports;

public sealed class CsvBankImportMapper
{
    public IReadOnlyList<AnalyzedMonthlyReportDraft> Map(string csvContent, Guid accountId, string dateColumn, string amountColumn, string? descriptionColumn, bool invertAmounts = false)
    {
        var rows = Parse(csvContent);
        if (rows.Count < 2)
        {
            throw new InvalidOperationException("CSV import must include a header row and at least one data row.");
        }

        var headers = rows[0].Select(static value => value.TrimStart('\uFEFF').Trim()).ToList();
        var dateIndex = FindHeaderIndex(headers, dateColumn, nameof(dateColumn));
        var amountIndex = FindHeaderIndex(headers, amountColumn, nameof(amountColumn));
        var descriptionIndex = string.IsNullOrWhiteSpace(descriptionColumn) ? -1 : FindHeaderIndex(headers, descriptionColumn, nameof(descriptionColumn));

        var drafts = new List<AnalyzedMonthlyReportDraft>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var dateValue = GetColumnValue(row, dateIndex);
            var amountValue = GetColumnValue(row, amountIndex);
            if (string.IsNullOrWhiteSpace(dateValue) && string.IsNullOrWhiteSpace(amountValue))
            {
                continue;
            }

            if (!DateTime.TryParse(dateValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var occurred))
            {
                throw new InvalidOperationException($"Unable to parse date '{dateValue}' on row {i + 1}.");
            }

            if (!decimal.TryParse(amountValue, NumberStyles.Number | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedAmount))
            {
                throw new InvalidOperationException($"Unable to parse amount '{amountValue}' on row {i + 1}.");
            }

            if (invertAmounts)
            {
                parsedAmount = -parsedAmount;
            }

            var type = parsedAmount >= 0 ? "Income" : "Expense";
            var amount = Math.Abs(parsedAmount);
            var note = descriptionIndex >= 0 ? GetColumnValue(row, descriptionIndex) : null;

            drafts.Add(new AnalyzedMonthlyReportDraft(
                $"csv-{i}",
                accountId,
                null,
                amount,
                type,
                DateTime.SpecifyKind(occurred, DateTimeKind.Utc),
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                1m,
                [],
                null,
                null,
                false,
                null,
                null,
                true));
        }

        return drafts;
    }

    private static int FindHeaderIndex(IReadOnlyList<string> headers, string expectedHeader, string fieldName)
    {
        var index = headers.Select((value, idx) => new { value, idx }).FirstOrDefault(item => string.Equals(item.value, expectedHeader, StringComparison.OrdinalIgnoreCase))?.idx ?? -1;
        if (index < 0)
        {
            throw new InvalidOperationException($"CSV is missing '{expectedHeader}' column for {fieldName}.");
        }

        return index;
    }

    private static string GetColumnValue(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

    private static IReadOnlyList<IReadOnlyList<string>> Parse(string content)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < content.Length; index++)
        {
            var current = content[index];
            if (current == '"')
            {
                if (inQuotes && index + 1 < content.Length && content[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (current == ',' && !inQuotes)
            {
                row.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            if ((current == '\r' || current == '\n') && !inQuotes)
            {
                if (current == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                continue;
            }

            cell.Append(current);
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows.Where(item => item.Any(cellValue => !string.IsNullOrWhiteSpace(cellValue))).ToList();
    }
}
