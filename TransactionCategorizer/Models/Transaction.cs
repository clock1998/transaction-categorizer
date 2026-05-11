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

public sealed record FileExtractionResult(
    int FileIndex,
    string FileName,
    ExtractionResult? Result,
    string? Error)
{
    public bool IsSuccess => Result is not null && Error is null;
}

public sealed record MultiFileExtractionResult(
    IReadOnlyList<FileExtractionResult> Files)
{
    public IReadOnlyList<FileExtractionResult> SuccessfulFiles => 
        Files.Where(f => f.IsSuccess).ToList();

    public IReadOnlyList<FileExtractionResult> FailedFiles => 
        Files.Where(f => !f.IsSuccess).ToList();

    public int TotalTransactions => 
        SuccessfulFiles.Sum(f => f.Result?.Transactions.Count ?? 0);

    public IReadOnlyList<TransactionWithSource> GetAllTransactions()
    {
        return SuccessfulFiles
            .SelectMany(f => f.Result!.Transactions.Select(t => 
                new TransactionWithSource(t, f.FileName, f.Result.StatementYear)))
            .ToList();
    }
}

public sealed record TransactionWithSource(
    Transaction Transaction,
    string SourceFile,
    int? StatementYear)
{
    public string Date => Transaction.Date;
    public string? PostDate => Transaction.PostDate;
    public string Description => Transaction.Description;
    public decimal Amount => Transaction.Amount;
    public string? Category => Transaction.Category;
    public string? TransactionSource => Transaction.TransactionSource;
}
