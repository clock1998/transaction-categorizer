namespace TransactionCategorizer;

public sealed class GeminiOptions
{
    public const string Section = "Gemini";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// HTTP request timeout in seconds. Default is 300 (5 minutes).
    /// Increase this value when processing multiple large PDFs.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum output tokens for Gemini response generation.
    /// Increase this for large multi-file extraction payloads.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 65536;
}
