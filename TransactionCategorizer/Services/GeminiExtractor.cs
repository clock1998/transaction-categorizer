using System.Text.Json.Nodes;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using TransactionCategorizer.Models;

namespace TransactionCategorizer.Services;

public sealed class GeminiExtractor
{

    private static readonly string PromptTemplate = """
        You are an expert financial data extraction assistant.

        Analyze the attached bank statement PDF and extract **every** transaction.

        Return a JSON **object** with the following structure:
        {{
          "statement_year": <int — the year the statement covers, e.g. 2025>,
          "transactions": [ ... ]
        }}

        Each item in the "transactions" array must have these fields:
        - "date": transaction date in YYYY/MM/DD format
        - "post_date": posting date in YYYY/MM/DD format (null if not available)
        - "description": merchant / payee description exactly as it appears
        - "amount": numeric amount (positive = debit/purchase, negative = credit/refund/payment)
        - "category": one of the allowed categories listed below
        - "transaction_source": the credit card name / product title shown on the statement (e.g. "DESJARDINS ODYSSEE WORLDELITE MASTERCARD", "Scotia Momentum VISA Infinite Card")

        Allowed categories:
        {0}

        Rules:
        1. Use ONLY the categories listed above. Pick the single best match.
        2. If the year is not explicitly shown on a transaction line, infer it from the statement date or surrounding context.
        3. Payments to the credit card itself should be negative amounts with category "Insurance and Financial Services".
        4. Return ONLY the JSON object described above — no markdown fences, no commentary.
        5. "statement_year" must be an integer representing the primary year the statement covers (e.g. if the statement period is Dec 2024 – Jan 2025, use the year that most transactions fall in).
        {1}
        """;

    private static readonly GenerateContentConfig GenerationConfig = new()
    {
        Temperature = 0.1f,
        ResponseMimeType = "application/json",
    };

    private readonly Client _client;
    private readonly string _model;
    private readonly ILogger<GeminiExtractor> _logger;
    private readonly IReadOnlyList<string> _categories;

    public GeminiExtractor(
        IOptions<GeminiOptions> options,
        IOptions<BudgetCategoriesOptions> categoriesOptions,
        ILogger<GeminiExtractor> logger)
    {
        _logger = logger;
        _categories = categoriesOptions.Value.Categories;

        var key = options.Value.ApiKey ?? System.Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "A Google API key is required. Set Gemini:ApiKey in configuration " +
                "or the GOOGLE_API_KEY environment variable.");

        _client = new Client(apiKey: key);
        _model = options.Value.Model;
    }

    public async Task<ExtractionResult> ExtractAsync(
        byte[] pdfBytes,
        IReadOnlyList<string>? categories = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        _logger.LogInformation("Sending PDF ({Bytes} bytes) to Gemini model {Model}", pdfBytes.Length, _model);

        var content = new Content
        {
            Role = "user",
            Parts =
            [
                Part.FromText(BuildPrompt(categories, context)),
                new Part { InlineData = new Blob { Data = pdfBytes, MimeType = "application/pdf" } },
            ],
        };

        var response = await _client.Models.GenerateContentAsync(
            model: _model,
            contents: [content],
            config: GenerationConfig,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = response.Text
            ?? throw new InvalidOperationException("Gemini response contained no text.");

        var raw = StripMarkdownFences(text);
        _logger.LogDebug("Gemini raw JSON: {Raw}", raw);
        return ParseTransactionJson(raw);
    }

    private string BuildPrompt(IReadOnlyList<string>? categories, string? context)
    {
        var categoryList = string.Join('\n', (categories ?? _categories).Select(c => $"- {c}"));
        var contextBlock = string.IsNullOrWhiteSpace(context)
            ? string.Empty
            : $"\nAdditional context provided by the user:\n{context}\n";

        return string.Format(PromptTemplate, categoryList, contextBlock);
    }

    private static string StripMarkdownFences(string text)
    {
        var s = text.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;

        var newline = s.IndexOf('\n');
        if (newline >= 0) s = s[(newline + 1)..];

        var closingFence = s.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0) s = s[..closingFence];

        return s.Trim();
    }

    private static ExtractionResult ParseTransactionJson(string json)
    {
        var root = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Gemini returned null JSON.");

        int? statementYear = null;
        JsonNode? arrayNode = root;

        if (root is JsonObject obj)
        {
            if (obj["statement_year"] is JsonValue yearVal && yearVal.TryGetValue(out int y))
                statementYear = y;

            foreach (var key in new[] { "transactions", "data", "results" })
            {
                if (obj[key] is JsonArray arr) { arrayNode = arr; break; }
            }
        }

        if (arrayNode is not JsonArray transactionArray)
            throw new InvalidOperationException(
                $"Expected a JSON array of transactions, got {root.GetType().Name}.");

        var transactions = transactionArray
            .OfType<JsonObject>()
            .Select(item => ParseTransaction(item))
            .ToList();

        return new ExtractionResult(statementYear, transactions);
    }

    private static Transaction ParseTransaction(JsonObject item) => new(
        Date: item["date"]?.GetValue<string>() ?? string.Empty,
        PostDate: item["post_date"]?.GetValue<string?>(),
        Description: item["description"]?.GetValue<string>() ?? string.Empty,
        Amount: item["amount"] is JsonValue amt ? (decimal)amt.GetValue<double>() : 0m,
        Category: item["category"]?.GetValue<string?>(),
        TransactionSource: item["transaction_source"]?.GetValue<string?>());
}
