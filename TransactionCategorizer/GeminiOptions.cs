namespace TransactionCategorizer;

public sealed class GeminiOptions
{
    public const string Section = "Gemini";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
