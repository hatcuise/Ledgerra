using System.Globalization;
using Ledgerra.Application.Imports;

namespace Ledgerra.Api.Services.Imports;

public sealed class CsvBankImportMapper
{
    public IReadOnlyList<AnalyzedMonthlyReportDraft> Map(string csvContent, Guid accountId, string dateColumn, string amountColumn, string? descriptionColumn, bool invertAmounts = false, CsvImportOptions? options = null)
    {
        options ??= new CsvImportOptions();
        options.Validate();
        var culture = CultureInfo.GetCultureInfo(options.CultureName);
        var rows = Parse(csvContent, options.Delimiter[0]);
        if (rows.Count < 2)
        {
            throw new InvalidOperationException("CSV import must include a header row and at least one data row.");
        }

        var headers = rows[0].Select(static value => value.Trim().TrimStart('\uFEFF').Trim()).ToList();
        var dateIndex = FindHeaderIndex(headers, dateColumn, nameof(dateColumn));
        var separateAmounts = !string.IsNullOrWhiteSpace(options.DebitColumn) || !string.IsNullOrWhiteSpace(options.CreditColumn);
        var amountIndex = separateAmounts ? -1 : FindHeaderIndex(headers, amountColumn, nameof(amountColumn));
        var debitIndex = string.IsNullOrWhiteSpace(options.DebitColumn) ? -1 : FindHeaderIndex(headers, options.DebitColumn, "debitColumn");
        var creditIndex = string.IsNullOrWhiteSpace(options.CreditColumn) ? -1 : FindHeaderIndex(headers, options.CreditColumn, "creditColumn");
        var typeIndex = string.IsNullOrWhiteSpace(options.TypeColumn) ? -1 : FindHeaderIndex(headers, options.TypeColumn, "typeColumn");
        var descriptionIndex = string.IsNullOrWhiteSpace(descriptionColumn) ? -1 : FindHeaderIndex(headers, descriptionColumn, nameof(descriptionColumn));

        var drafts = new List<AnalyzedMonthlyReportDraft>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var dateValue = GetColumnValue(row, dateIndex);
            var amountValue = GetColumnValue(row, amountIndex);
            var debit = GetColumnValue(row, debitIndex);
            var credit = GetColumnValue(row, creditIndex);
            if (string.IsNullOrWhiteSpace(dateValue) && string.IsNullOrWhiteSpace(amountValue) && string.IsNullOrWhiteSpace(debit) && string.IsNullOrWhiteSpace(credit))
            {
                continue;
            }

            DateTime occurred;
            var validDate = string.IsNullOrWhiteSpace(options.DateFormat)
                ? DateTime.TryParse(dateValue, culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out occurred)
                : DateTime.TryParseExact(dateValue, options.DateFormat, culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out occurred);
            if (!validDate)
            {
                throw new InvalidOperationException($"Unable to parse date '{dateValue}' on row {i + 1}.");
            }

            decimal ReadAmount(string value)
            {
                if (!decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowParentheses, culture, out var result))
                    throw new InvalidOperationException($"Unable to parse amount '{value}' on row {i + 1}.");
                return result;
            }
            decimal parsedAmount;
            if (separateAmounts)
            {
                var debitAmount = string.IsNullOrWhiteSpace(debit) ? 0 : ReadAmount(debit);
                var creditAmount = string.IsNullOrWhiteSpace(credit) ? 0 : ReadAmount(credit);
                if (debitAmount < 0 || creditAmount < 0 || (debitAmount != 0 && creditAmount != 0))
                    throw new InvalidOperationException($"Row {i + 1} must have a nonnegative debit or credit, not both.");
                parsedAmount = creditAmount - debitAmount;
            }
            else parsedAmount = ReadAmount(amountValue);
            if (parsedAmount == 0) continue;
            if (invertAmounts) parsedAmount = -parsedAmount;
            var type = parsedAmount >= 0 ? options.PositiveType : options.NegativeType;
            if (typeIndex >= 0)
            {
                var explicitType = GetColumnValue(row, typeIndex);
                type = CsvImportOptions.NormalizeType(explicitType);
            }
            string? OptionalCell(string? column, int maxLength)
            {
                if (string.IsNullOrWhiteSpace(column)) return null;
                var value = GetColumnValue(row, FindHeaderIndex(headers, column, column));
                if (value.Length > maxLength) throw new InvalidOperationException($"Column '{column}' exceeds {maxLength} characters on row {i + 1}.");
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            var payee = OptionalCell(options.PayeeColumn, 120);
            var reference = OptionalCell(options.ReferenceColumn, 120);
            var tags = OptionalCell(options.TagsColumn, 400);
            var categoryName = OptionalCell(options.CategoryColumn, 120);
            var amount = Math.Abs(parsedAmount);
            var note = descriptionIndex >= 0 ? GetColumnValue(row, descriptionIndex) : null;

            drafts.Add(new AnalyzedMonthlyReportDraft(
                $"csv-{i}",
                accountId,
                null,
                amount,
                type,
                DateTime.SpecifyKind(occurred, DateTimeKind.Utc),
                string.IsNullOrWhiteSpace(note) ? payee : note.Trim(),
                1m,
                [],
                null,
                null,
                false,
                null,
                null,
                true, payee, reference, tags, categoryName));
        }

        return drafts;
    }

    private static int FindHeaderIndex(IReadOnlyList<string> headers, string expectedHeader, string fieldName)
    {
        expectedHeader = expectedHeader.Trim().TrimStart('\uFEFF').Trim();
        var matches = headers.Select((value, idx) => new { value, idx }).Where(item => string.Equals(item.value, expectedHeader, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new InvalidOperationException($"CSV has multiple '{expectedHeader}' columns. Rename duplicate headers before importing.");
        var index = matches.FirstOrDefault()?.idx ?? -1;
        if (index < 0)
        {
            throw new InvalidOperationException($"CSV is missing '{expectedHeader}' column for {fieldName}.");
        }

        return index;
    }

    private static string GetColumnValue(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

    private static IReadOnlyList<IReadOnlyList<string>> Parse(string content, char delimiter)
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

            if (current == delimiter && !inQuotes)
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

        if (inQuotes) throw new InvalidOperationException("CSV contains an unclosed quoted field.");

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows.Where(item => item.Any(cellValue => !string.IsNullOrWhiteSpace(cellValue))).ToList();
    }
}
