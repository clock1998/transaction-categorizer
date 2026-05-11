using System.Text.Json.Nodes;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using TransactionCategorizer.Models;

namespace TransactionCategorizer.Services;

public sealed class GeminiExtractor
{

    private static readonly string PromptTemplate = """
        You are an expert financial data extraction assistant.

        Analyze the attached bank statement PDF(s) and extract **every** transaction from **each file**.

        {2}

        Return a JSON **array** where each element represents one file with the following structure:
        [
          {{
            "file_index": 0,
            "statement_year": <int — the year the statement covers>,
            "transactions": [ ... ]
          }},
          {{
            "file_index": 1,
            "statement_year": <int>,
            "transactions": [ ... ]
          }}
        ]

        Each item in the "transactions" array must have these fields:
        - "date": transaction date in YYYY/MM/DD format
        - "post_date": posting date in YYYY/MM/DD format (null if not available)
        - "description": merchant / payee description exactly as it appears
        - "amount": numeric amount (positive = debit/purchase, negative = credit/refund/payment)
        - "category": one of the allowed categories listed below
        - "transaction_source": the credit card name / product title shown on the statement

        Allowed categories:
        {0}

        Rules:
        1. Use ONLY the categories listed above. Pick the single best match.
        2. Process each PDF separately and maintain the file order.
        3. Each file result MUST include "file_index" matching the PDF order (0, 1, 2, etc.).
        4. If the year is not explicitly shown, infer it from the statement date or context.
        5. Payments to the credit card should be negative amounts with category "Insurance and Financial Services".
        6. Return ONLY the JSON array described above — no markdown fences, no commentary.
        7. Keep JSON compact (no pretty printing or extra whitespace) to minimize output size.
        {1}
        """;

    private readonly Client _client;
    private readonly string _model;
    private readonly ILogger<GeminiExtractor> _logger;
    private readonly IReadOnlyList<string> _categories;
    private readonly int _timeoutSeconds;
    private readonly int _maxOutputTokens;
    private readonly GenerateContentConfig _generationConfig;

    public GeminiExtractor(
        IOptions<GeminiOptions> options,
        IOptions<BudgetCategoriesOptions> categoriesOptions,
        ILogger<GeminiExtractor> logger)
    {
        _logger = logger;
        _categories = categoriesOptions.Value.Categories;

        var geminiOptions = options.Value;

        // Validate API key
        if (string.IsNullOrWhiteSpace(geminiOptions.ApiKey))
            throw new InvalidOperationException(
                "A Gemini API key is required. Set Gemini:ApiKey in configuration " +
                "or the Gemini__ApiKey environment variable.");

        _timeoutSeconds = geminiOptions.TimeoutSeconds;
        _maxOutputTokens = geminiOptions.MaxOutputTokens;
        _model = geminiOptions.Model;

        if (_timeoutSeconds <= 0)
            throw new InvalidOperationException("Gemini:TimeoutSeconds must be greater than 0.");

        if (_maxOutputTokens <= 0)
            throw new InvalidOperationException("Gemini:MaxOutputTokens must be greater than 0.");

        _logger.LogInformation(
            "Initializing Gemini client with model {Model}, timeout {TimeoutSeconds}s, max output tokens {MaxOutputTokens}",
            _model,
            _timeoutSeconds,
            _maxOutputTokens);

        var httpOptions = new HttpOptions
        {
            Timeout = checked(_timeoutSeconds * 1000)
        };

        _client = new Client(apiKey: geminiOptions.ApiKey, httpOptions: httpOptions);
        _generationConfig = new GenerateContentConfig
        {
            Temperature = 0.1f,
            ResponseMimeType = "application/json",
            MaxOutputTokens = _maxOutputTokens,
            HttpOptions = httpOptions
        };
    }

    public async Task<MultiFileExtractionResult> ExtractMultipleAsync(
        IReadOnlyList<byte[]> pdfBytesList,
        IReadOnlyList<string>? fileNames = null,
        IReadOnlyList<string>? categories = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytesList);

        if (pdfBytesList.Count == 0)
            return new MultiFileExtractionResult([]);

        _logger.LogInformation("Sending {Count} PDF(s) in a single request to Gemini model {Model}", 
            pdfBytesList.Count, _model);

        // Create a timeout cancellation token source
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var prompt = BuildPrompt(pdfBytesList.Count, fileNames, categories, context);
            var parts = new List<Part> { Part.FromText(prompt) };

            // Add all PDF files as separate parts
            foreach (var pdfBytes in pdfBytesList)
            {
                parts.Add(new Part 
                { 
                    InlineData = new Blob { Data = pdfBytes, MimeType = "application/pdf" } 
                });
            }

            var content = new Content { Role = "user", Parts = [.. parts] };

            var response = await _client.Models.GenerateContentAsync(
                model: _model,
                contents: [content],
                config: _generationConfig,
                cancellationToken: linkedCts.Token).ConfigureAwait(false);

            var rawJson = ExtractJsonFromResponse(response);
            _logger.LogDebug("Gemini raw JSON for {Count} file(s): {Raw}", pdfBytesList.Count, rawJson);

            return ParseMultiFileJson(rawJson, fileNames);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var timeoutMessage = $"Request timed out after {_timeoutSeconds} seconds. " +
                $"Consider increasing Gemini:TimeoutSeconds in configuration for large batches.";

            _logger.LogError("Failed to process {Count} file(s) in batch: {Error}", 
                pdfBytesList.Count, timeoutMessage);

            // Return error result for all files
            var results = pdfBytesList.Select((_, index) =>
            {
                var fileName = GetFileName(index, fileNames);
                return new FileExtractionResult(index, fileName, null, timeoutMessage);
            }).ToList();

            return new MultiFileExtractionResult(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process {Count} file(s) in batch", pdfBytesList.Count);

            // Return error result for all files
            var results = pdfBytesList.Select((_, index) =>
            {
                var fileName = GetFileName(index, fileNames);
                return new FileExtractionResult(index, fileName, null, ex.Message);
            }).ToList();

            return new MultiFileExtractionResult(results);
        }
    }

    private string BuildPrompt(
        int fileCount, 
        IReadOnlyList<string>? fileNames, 
        IReadOnlyList<string>? categories, 
        string? context)
    {
        var fileListBlock = BuildFileListBlock(fileCount, fileNames);
        var categoryList = FormatCategoryList(categories);
        var contextBlock = FormatContextBlock(context);
        return string.Format(PromptTemplate, categoryList, contextBlock, fileListBlock);
    }

    private static string BuildFileListBlock(int fileCount, IReadOnlyList<string>? fileNames)
    {
        if (fileNames is null || fileNames.Count == 0)
            return $"You will receive {fileCount} PDF file(s) in order.";

        var fileList = string.Join('\n', fileNames.Select((name, i) => $"  {i}. {name}"));
        return $"You will receive {fileCount} PDF file(s) in the following order:\n{fileList}";
    }

    private string FormatCategoryList(IReadOnlyList<string>? categories)
    {
        var categoriesToUse = categories ?? _categories;
        return string.Join('\n', categoriesToUse.Select(c => $"- {c}"));
    }

    private static string FormatContextBlock(string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return string.Empty;

        return $"\nAdditional context provided by the user:\n{context}\n";
    }

    private static string ExtractJsonFromResponse(GenerateContentResponse response)
    {
        var text = response.Text
            ?? throw new InvalidOperationException("Gemini response contained no text.");

        var raw = StripMarkdownFences(text);
        return ExtractJsonArrayPayload(raw);
    }

    private static string ExtractJsonArrayPayload(string text)
    {
        var firstBracket = text.IndexOf('[');
        if (firstBracket < 0)
            return text;

        var lastBracket = text.LastIndexOf(']');
        if (lastBracket < firstBracket)
            throw new InvalidOperationException(
                "Gemini response appears truncated before JSON array completed. Increase Gemini:MaxOutputTokens or reduce files per batch.");

        return text[firstBracket..(lastBracket + 1)].Trim();
    }

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        // Remove opening fence and language identifier
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
            trimmed = trimmed[(firstNewline + 1)..];

        // Remove closing fence
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0)
            trimmed = trimmed[..lastFence];

        return trimmed.Trim();
    }

    private static MultiFileExtractionResult ParseMultiFileJson(
        string json, 
        IReadOnlyList<string>? fileNames)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json)
                ?? throw new InvalidOperationException("Gemini returned null JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Gemini returned invalid JSON for multi-file extraction. Increase Gemini:MaxOutputTokens or reduce files per batch.",
                ex);
        }

        if (root is not JsonArray fileArray)
            throw new InvalidOperationException(
                $"Expected a JSON array for multiple files, got {root.GetType().Name}.");

        var results = new List<FileExtractionResult>();

        foreach (var fileNode in fileArray)
        {
            if (fileNode is not JsonObject fileObj)
                continue;

            var fileIndex = fileObj["file_index"]?.GetValue<int>() ?? results.Count;
            var fileName = GetFileName(fileIndex, fileNames);
            var statementYear = TryGetStatementYear(fileObj);
            var transactions = ExtractTransactionsFromObject(fileObj);

            var extractionResult = new ExtractionResult(statementYear, transactions);
            results.Add(new FileExtractionResult(fileIndex, fileName, extractionResult, null));
        }

        return new MultiFileExtractionResult(results);
    }

    private static string GetFileName(int index, IReadOnlyList<string>? fileNames)
    {
        return fileNames is not null && index < fileNames.Count 
            ? fileNames[index] 
            : $"File {index + 1}";
    }

    private static int? TryGetStatementYear(JsonObject obj)
    {
        if (obj["statement_year"] is JsonValue yearValue && yearValue.TryGetValue(out int year))
            return year;

        return null;
    }

    private static IReadOnlyList<Transaction> ExtractTransactionsFromObject(JsonObject obj)
    {
        // Try common transaction array keys
        JsonArray? transactionArray = null;
        foreach (var key in new[] { "transactions", "data", "results" })
        {
            if (obj[key] is JsonArray arr)
            {
                transactionArray = arr;
                break;
            }
        }

        if (transactionArray is null)
            throw new InvalidOperationException("No transaction array found in JSON object.");

        return transactionArray
            .OfType<JsonObject>()
            .Select(ParseTransaction)
            .ToList();
    }

    private static Transaction ParseTransaction(JsonObject item)
    {
        var date = item["date"]?.GetValue<string>() ?? string.Empty;
        var postDate = item["post_date"]?.GetValue<string?>();
        var description = item["description"]?.GetValue<string>() ?? string.Empty;
        var amount = item["amount"] is JsonValue amountValue 
            ? (decimal)amountValue.GetValue<double>() 
            : 0m;
        var category = item["category"]?.GetValue<string?>();
        var transactionSource = item["transaction_source"]?.GetValue<string?>();

        return new Transaction(date, postDate, description, amount, category, transactionSource);
    }
}
