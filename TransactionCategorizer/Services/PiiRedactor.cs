using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace TransactionCategorizer.Services;

/// <summary>
/// Detects and redacts personal identifiable information (PII) from PDF documents.
///
/// Targets the following PII types commonly found in bank statements:
///   - Credit/debit card numbers (16-digit sequences)
///   - Masked card numbers (e.g. ****-****-****-1234)
///   - Bank account and reference numbers (near identifying keywords)
///   - Personal names (via context keywords and address block detection)
///   - Mailing addresses (street, city, province, postal code)
///   - Phone numbers
///   - Email addresses
/// </summary>
public sealed class PiiRedactor
{
    #region Regex Patterns

    // Credit/debit card: 4 groups of 4 digits
    private static readonly Regex CardPattern = new(
        @"\b(\d{4}[\s\-]{0,2}\d{4}[\s\-]{0,2}\d{4}[\s\-]{0,2}\d{4})\b",
        RegexOptions.Compiled);

    // Masked card numbers: ****-****-****-1234 variants
    private static readonly Regex MaskedCardPattern = new(
        @"[*Xx]{4}[\s\-]{0,2}[*Xx]{4}[\s\-]{0,2}[*Xx]{4}[\s\-]{0,2}\d{4}",
        RegexOptions.Compiled);

    // Partially masked card: 5598 28** **** 8007 (digits + asterisks mixed)
    private static readonly Regex PartialMaskedCardPattern = new(
        @"\b\d{4}[\s\-]{0,2}\d{0,2}[*]{2,4}[\s\-]{0,2}[*]{4}[\s\-]{0,2}\d{4}\b",
        RegexOptions.Compiled);

    // Phone: (XXX) XXX-XXXX or XXX-XXX-XXXX or XXX.XXX.XXXX
    private static readonly Regex PhonePattern = new(
        @"\(?\b\d{3}\)?[\s\-\.]{1,2}\d{3}[\s\-\.]\d{4}\b",
        RegexOptions.Compiled);

    // Email addresses
    private static readonly Regex EmailPattern = new(
        @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

    // Canadian postal code: A1A 1A1
    private static readonly Regex PostalCodePattern = new(
        @"\b([A-Za-z]\d[A-Za-z][\s\-]?\d[A-Za-z]\d)\b",
        RegexOptions.Compiled);

    // Account/card numbers near identifying keywords (EN + FR)
    private static readonly Regex[] AccountPatterns =
    [
        new Regex(
            @"(?:Account|Compte|Carte|Card)" +
            @"[\s]*(?:Number|No\.?|#|number|num[ée]ro|de compte|de carte)?" +
            @"[\s:]*([\d][\d\s\-\.]{4,24}[\d])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(
            @"(?:Num[ée]ro)[\s]*(?:de\s+)?(?:compte|carte|r[ée]f[ée]rence)" +
            @"[\s:]*([\d][\d\s\-\.]{4,24}[\d])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // Name near context keywords (captures to end of line)
    private static readonly Regex[] NamePatterns =
    [
        new Regex(
            @"(?:Account\s*Holder|Card\s*(?:Member|Holder)|" +
            @"Nom\s*(?:du\s*)?(?:titulaire|client|d[ée]tenteur|membre)|" +
            @"Titulaire|Monsieur|Madame|M\.|Mme|Mr\.?|Mrs\.?|Ms\.?)" +
            @"[\s:,]+(.+?)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),
        // French: "carte de : NAME" / "effectuées ... carte de : NAME"
        new Regex(
            @"(?:carte\s+de|carte\s+de\s+cr[ée]dit\s+de)[\s:]+" +
            @"([A-Z\u00C0-\u024F][A-Za-z\u00C0-\u024F\-\']+" +
            @"(?:\s+[A-Z\u00C0-\u024F][A-Za-z\u00C0-\u024F\-\']+)+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // Street address: civic number + street name + street type
    private static readonly Regex AddressPattern = new(
        @"\b(\d{1,5}\s+" +
        @"(?:[\w'\u00C0-\u024F\-]+\s+){1,5}" +
        @"(?:Street|St\.?|Avenue|Ave\.?|Boulevard|Blvd\.?|Road|Rd\.?|" +
        @"Drive|Dr\.?|Lane|Ln\.?|Court|Ct\.?|Place|Pl\.?|Way|" +
        @"Rue|Chemin|Ch\.?|Boul\.?|Av\.?|Cres\.?|Crescent|" +
        @"Circle|Cir\.?|Terrace|Terr\.?|Trail|Trl\.?|" +
        @"Parkway|Pkwy\.?|Highway|Hwy\.?)(?:\.|\b))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // City + Province/Territory (Canadian)
    private static readonly Regex CityProvPattern = new(
        @"([\w\u00C0-\u024F]+(?:[\s\-][\w\u00C0-\u024F]+)*)[\s,]+" +
        @"(QC|ON|BC|AB|MB|SK|NB|NS|PE|NL|NT|NU|YT|" +
        @"Qu[ée]bec|Ontario|British\s+Columbia|Alberta|Manitoba|" +
        @"Saskatchewan|New\s+Brunswick|Nova\s+Scotia|" +
        @"Prince\s+Edward\s+Island|Newfoundland(?:\s+and\s+Labrador)?)" +
        @"(?:[\s,]+[A-Za-z]\d[A-Za-z][\s\-]?\d[A-Za-z]\d)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    #endregion

    /// <summary>
    /// Redacts all detected PII from a PDF document.
    /// </summary>
    /// <param name="pdfBytes">Raw bytes of the original PDF.</param>
    /// <returns>Bytes of the redacted PDF with PII replaced by black rectangles.</returns>
    public byte[] Redact(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        using var outputStream = new MemoryStream();

        using (var reader = new PdfReader(new MemoryStream(pdfBytes)))
        using (var writer = new PdfWriter(outputStream))
        using (var doc = new PdfDocument(reader, writer))
        {
            int pageCount = doc.GetNumberOfPages();

            // Phase 1: scan full document text for names
            var sb = new StringBuilder();
            for (int i = 1; i <= pageCount; i++)
                sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));

            var knownNames = ExtractNames(sb.ToString());

            // Phase 1b: extract names from address blocks on first page
            if (pageCount > 0)
                ExtractNamesFromAddressBlocks(doc.GetPage(1), knownNames);

            // Phase 2: redact each page
            for (int i = 1; i <= pageCount; i++)
                RedactPage(doc.GetPage(i), knownNames, isFirstPage: i == 1);
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Redacts PII from a PDF file and saves the result.
    /// </summary>
    /// <param name="inputPath">Path to the original PDF.</param>
    /// <param name="outputPath">Path to save the redacted PDF.</param>
    public void RedactFile(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var redacted = Redact(File.ReadAllBytes(inputPath));
        File.WriteAllBytes(outputPath, redacted);
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private static HashSet<string> ExtractNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in NamePatterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                var name = match.Groups[1].Value.Trim();
                if (name.Length >= 3 && !name.All(char.IsDigit))
                    AddNameVariants(names, name);
            }
        }
        return names;
    }

    private static void ExtractNamesFromAddressBlocks(PdfPage page, HashSet<string> names)
    {
        var extractor = new TextBlockExtractor();
        new PdfCanvasProcessor(extractor).ProcessPageContent(page);

        foreach (var (blockText, _) in extractor.GetBlocks())
        {
            if (!PostalCodePattern.IsMatch(blockText)) continue;

            // The first line of an address block is usually the recipient name
            var firstLine = blockText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                     .FirstOrDefault()
                                     ?.Trim();

            if (firstLine is { Length: >= 3 }
                && !char.IsDigit(firstLine[0])
                && !PostalCodePattern.IsMatch(firstLine))
            {
                AddNameVariants(names, firstLine);
            }
        }
    }

    private static void AddNameVariants(HashSet<string> names, string name)
    {
        names.Add(name);
        names.Add(name.ToUpperInvariant());
        names.Add(System.Globalization.CultureInfo.InvariantCulture.TextInfo
                       .ToTitleCase(name.ToLowerInvariant()));

        var collapsed = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed == name) return;

        names.Add(collapsed);
        names.Add(collapsed.ToUpperInvariant());
        names.Add(System.Globalization.CultureInfo.InvariantCulture.TextInfo
                        .ToTitleCase(collapsed.ToLowerInvariant()));
    }

    private static void RedactPage(PdfPage page, IReadOnlySet<string> knownNames, bool isFirstPage)
    {
        var text = PdfTextExtractor.GetTextFromPage(page);
        if (string.IsNullOrEmpty(text)) return;

        var piiTexts = new HashSet<string>(StringComparer.Ordinal);
        CollectPatternMatches(text, piiTexts);
        piiTexts.UnionWith(knownNames);

        var rects = new List<Rectangle>();
        foreach (var piiText in piiTexts)
            rects.AddRange(FindTextLocations(page, piiText));

        // On the first page, also redact the full mailing address block
        if (isFirstPage)
            rects.AddRange(FindAddressBlockRects(page));

        ApplyBlackRects(page, rects);
    }

    private static IEnumerable<Rectangle> FindTextLocations(PdfPage page, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var strategy = new RegexBasedLocationExtractionStrategy(
            new Regex(Regex.Escape(text), RegexOptions.IgnoreCase));

        new PdfCanvasProcessor(strategy).ProcessPageContent(page);

        foreach (var location in strategy.GetResultantLocations())
            yield return location.GetRectangle();
    }

    /// <summary>
    /// Finds bounding rectangles for text blocks that contain a Canadian postal code.
    /// Bank statements typically place the full mailing address in such a block.
    /// </summary>
    private static IEnumerable<Rectangle> FindAddressBlockRects(PdfPage page)
    {
        var extractor = new TextBlockExtractor();
        new PdfCanvasProcessor(extractor).ProcessPageContent(page);

        foreach (var (blockText, bounds) in extractor.GetBlocks())
        {
            if (PostalCodePattern.IsMatch(blockText))
                yield return bounds;
        }
    }

    private static void ApplyBlackRects(PdfPage page, IReadOnlyList<Rectangle> rects)
    {
        if (rects.Count == 0) return;

        // Append a new content stream drawn on top of the existing page content
        var canvas = new PdfCanvas(
            page.NewContentStreamAfter(),
            page.GetResources(),
            page.GetDocument());

        canvas.SetFillColor(ColorConstants.BLACK);
        foreach (var rect in rects)
            canvas.Rectangle(rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight())
                  .Fill();

        canvas.Release();
    }

    private static void CollectPatternMatches(string text, HashSet<string> results)
    {
        foreach (Match m in CardPattern.Matches(text))
            results.Add(m.Groups[1].Value.Trim());

        foreach (Match m in MaskedCardPattern.Matches(text))
            results.Add(m.Value.Trim());

        foreach (Match m in PartialMaskedCardPattern.Matches(text))
            results.Add(m.Value.Trim());

        foreach (Match m in PhonePattern.Matches(text))
            results.Add(m.Value.Trim());

        foreach (Match m in EmailPattern.Matches(text))
            results.Add(m.Value.Trim());

        foreach (Match m in PostalCodePattern.Matches(text))
            results.Add(m.Groups[1].Value.Trim());

        foreach (var pattern in AccountPatterns)
        {
            foreach (Match m in pattern.Matches(text))
            {
                var value = m.Groups[1].Value.Trim();
                // Require at least 4 digits to avoid false positives
                if (Regex.Replace(value, @"\D", "").Length >= 4)
                    results.Add(value);
            }
        }

        foreach (Match m in AddressPattern.Matches(text))
            results.Add(m.Groups[1].Value.Trim());

        foreach (Match m in CityProvPattern.Matches(text))
            results.Add(m.Value.Trim());
    }

    // ── Text block extraction ─────────────────────────────────────────────

    /// <summary>
    /// iText7 event listener that collects text chunks with their bounding boxes,
    /// then groups them into lines and blocks based on spatial proximity.
    /// </summary>
    private sealed class TextBlockExtractor : IEventListener
    {
        private record struct Chunk(string Text, float X, float Y, float Right, float Top);

        private readonly List<Chunk> _chunks = [];

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo info) return;

            var text = info.GetText();
            if (string.IsNullOrEmpty(text)) return;

            var baseline = info.GetBaseline();
            var ascent = info.GetAscentLine();
            var descent = info.GetDescentLine();

            float x1 = Math.Min(
                baseline.GetStartPoint().Get(Vector.I1),
                baseline.GetEndPoint().Get(Vector.I1));
            float x2 = Math.Max(
                baseline.GetStartPoint().Get(Vector.I1),
                baseline.GetEndPoint().Get(Vector.I1));
            float y1 = descent.GetStartPoint().Get(Vector.I2);
            float y2 = ascent.GetStartPoint().Get(Vector.I2);

            _chunks.Add(new Chunk(text, x1, y1, x2, y2));
        }

        public ICollection<EventType> GetSupportedEvents() => [EventType.RENDER_TEXT];

        /// <summary>
        /// Groups chunks into text blocks and returns each block's concatenated text
        /// (newline-separated lines) along with its bounding rectangle.
        /// </summary>
        public IEnumerable<(string BlockText, Rectangle Bounds)> GetBlocks(
            float lineToleranceY = 3f,
            float blockGapY = 12f)
        {
            if (_chunks.Count == 0) yield break;

            // Sort top-to-bottom (highest Y first in PDF space), left-to-right
            var sorted = _chunks
                .OrderByDescending(c => (c.Y + c.Top) / 2f)
                .ThenBy(c => c.X)
                .ToList();

            // Group into lines by similar Y midpoint
            var lines = new List<List<Chunk>>();
            List<Chunk>? currentLine = null;
            float lastMidY = float.MaxValue;

            foreach (var chunk in sorted)
            {
                float midY = (chunk.Y + chunk.Top) / 2f;
                if (currentLine is null || Math.Abs(midY - lastMidY) > lineToleranceY)
                {
                    currentLine = [chunk];
                    lines.Add(currentLine);
                    lastMidY = midY;
                }
                else
                {
                    currentLine.Add(chunk);
                    lastMidY = (lastMidY + midY) / 2f; // running average
                }
            }

            // Group lines into blocks by vertical gap between consecutive lines
            var blocks = new List<List<List<Chunk>>>();
            List<List<Chunk>>? currentBlock = null;
            float prevLineBottom = float.MaxValue;

            foreach (var line in lines)
            {
                float lineTop = line.Max(c => c.Top);
                float lineBottom = line.Min(c => c.Y);

                if (currentBlock is null || prevLineBottom - lineTop > blockGapY)
                {
                    currentBlock = [line];
                    blocks.Add(currentBlock);
                }
                else
                {
                    currentBlock.Add(line);
                }

                prevLineBottom = lineBottom;
            }

            // Yield each block as (text, bounding rect)
            foreach (var block in blocks)
            {
                var all = block.SelectMany(l => l).ToList();
                var blockText = string.Join('\n', block.Select(line =>
                    string.Concat(line.OrderBy(c => c.X).Select(c => c.Text))));

                float minX = all.Min(c => c.X);
                float minY = all.Min(c => c.Y);
                float maxX = all.Max(c => c.Right);
                float maxY = all.Max(c => c.Top);

                yield return (blockText, new Rectangle(minX, minY, maxX - minX, maxY - minY));
            }
        }
    }
}
