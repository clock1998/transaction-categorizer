namespace TransactionCategorizer;

public sealed class BudgetCategoriesOptions
{
    public const string Section = "BudgetCategories";

    public IReadOnlyList<string> Categories { get; set; } = [];
}
