using System.Text;
using TransactionCategorizer.Models;

namespace TransactionCategorizer.Services;

/// <summary>Serializes an <see cref="ExtractionResult"/> to UTF-8 CSV bytes.</summary>
public static class CsvExporter
{
    private static readonly string[] Headers =
    [
        "Date",
        "Post Date",
        "Description",
        "Amount",
        "Category",
        "Transaction Source",
        "Statement Year",
    ];

    /// <summary>
    /// Converts an extraction result to CSV bytes (UTF-8 with BOM for Excel compatibility).
    /// </summary>
    public static byte[] Export(ExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        AppendRow(sb, Headers);

        foreach (var t in result.Transactions)
        {
            AppendRow(sb,
            [
                t.Date,
                t.PostDate ?? string.Empty,
                t.Description,
                t.Amount.ToString("F2"),
                t.Category ?? string.Empty,
                t.TransactionSource ?? string.Empty,
                result.StatementYear?.ToString() ?? string.Empty,
            ]);
        }

        // UTF-8 BOM so Excel opens the file correctly
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> fields)
    {
        sb.AppendJoin(',', fields.Select(EscapeField));
        sb.Append('\n');
    }

    /// <summary>Wraps a field in double-quotes if it contains commas, quotes, or newlines.</summary>
    private static string EscapeField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n');
        if (!needsQuoting) return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
