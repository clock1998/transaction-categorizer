namespace TransactionCategorizer.Models;

public sealed record Transaction(
    string Date,
    string? PostDate,
    string Description,
    decimal Amount,
    string? Category,
    string? TransactionSource);

public sealed record ExtractionResult(
    int? StatementYear,
    IReadOnlyList<Transaction> Transactions);
