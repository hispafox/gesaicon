using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Gesaicon.Api.Services;

public interface IReceiptAnalysisProcessor
{
    Task<bool> AnalyzeAsync(int ticketId, int attempt, CancellationToken ct);
}

public sealed class ReceiptAnalysisProcessor : IReceiptAnalysisProcessor
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ReceiptAnalysisProcessor> _logger;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnalysisPromptProvider _promptProvider;

    public ReceiptAnalysisProcessor(
        IServiceProvider sp,
        ILogger<ReceiptAnalysisProcessor> logger,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        IAnalysisPromptProvider promptProvider)
    {
        _sp = sp;
        _logger = logger;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _promptProvider = promptProvider;
    }

    public async Task<bool> AnalyzeAsync(int ticketId, int attempt, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var pathStrategy = scope.ServiceProvider.GetRequiredService<IBusinessFilePathStrategy>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        var ticket = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket == null)
        {
            _logger.LogWarning("[Processor] Ticket {Id} no encontrado", ticketId);
            return false;
        }
        if (ticket.Status == "Completed") return true;

        ticket.Status = "Processing";
        await db.SaveChangesAsync(ct);

        var log = new AnalysisLog
        {
            TicketId = ticket.Id,
            StartedAt = DateTime.UtcNow,
            Operation = "Queue",
            Attempt = attempt,
            FileHashSnapshot = ticket.FileHash
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var apiKey = _config["GROQ:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ticket.Status = "Error"; ticket.LastErrorMessage = "API Key no configurada";
                log.Success = false; log.ErrorMessage = "API Key not configured"; return false;
            }
            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
            var model = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
            log.Model = model; log.Endpoint = endpoint;

            string? filePath = null;
            if (!string.IsNullOrWhiteSpace(ticket.RelativePath)) filePath = storage.GetPhysicalPath(ticket.RelativePath);
            else if (!string.IsNullOrWhiteSpace(ticket.FileName)) filePath = Path.Combine(env.ContentRootPath, "Uploads", ticket.FileName);
            if (filePath == null || !File.Exists(filePath))
            { ticket.Status = "Error"; ticket.LastErrorMessage = "Archivo no encontrado"; log.Success = false; log.ErrorMessage = "File not found"; return false; }

            var optimizedBytes = await OptimizeImageAsync(filePath, ct);
            var dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(optimizedBytes)}";
            var prompt = _promptProvider.GetPrompt();
            var payload = new { model, messages = new object[]{ new { role="user", content = new object[]{ new { type="text", text = prompt }, new { type="image_url", image_url = new { url = dataUrl } } } } }, temperature=0, max_completion_tokens=500 };
            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var httpContent = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(endpoint, httpContent, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            { ticket.Status = "Error"; ticket.LastErrorMessage = $"Error HTTP {(int)resp.StatusCode}"; log.Success=false; log.ErrorMessage=$"HTTP {(int)resp.StatusCode}"; return false; }

            string? contentText = null;
            try { using var doc = System.Text.Json.JsonDocument.Parse(body); contentText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (doc.RootElement.TryGetProperty("usage", out var usageEl)) { log.JsonUsageRaw = usageEl.GetRawText(); if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind==System.Text.Json.JsonValueKind.Number) log.PromptTokens=pt.GetInt32(); if (usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind==System.Text.Json.JsonValueKind.Number) log.CompletionTokens=ctEl.GetInt32(); if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind==System.Text.Json.JsonValueKind.Number) log.TotalTokens=tt.GetInt32(); } }
            catch { contentText = body; }

            string? markdownPart = null; string? jsonPart = contentText;
            if (!string.IsNullOrWhiteSpace(contentText)) { var splitIndex = contentText.IndexOf("}\n\n"); if (splitIndex < 0) splitIndex = contentText.IndexOf("}\r\n\r\n"); if (splitIndex > 0) { jsonPart = contentText[..(splitIndex+1)].Trim(); markdownPart = contentText[(splitIndex+3)..].Trim(); } }
            var jsonExtract = ExtractJson(jsonPart ?? string.Empty);
            if (jsonExtract == null) { ticket.Status="Error"; ticket.LastErrorMessage="No se encontró JSON"; log.Success=false; log.ErrorMessage="No JSON"; return false; }

            try
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(jsonExtract);
                if (parsed.RootElement.TryGetProperty("Amount", out var aEl))
                {
                    if (aEl.ValueKind==System.Text.Json.JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) ticket.Amount=dec;
                    else if (decimal.TryParse(aEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec2)) ticket.Amount=dec2;
                }
                if (parsed.RootElement.TryGetProperty("Company", out var cEl)) ticket.CompanyName = cEl.GetString();
                if (parsed.RootElement.TryGetProperty("Category", out var catEl)) ticket.Category = catEl.GetString();
                ticket.PurchaseDate = ExtractDate(parsed.RootElement);
                if (ticket.PurchaseDate.HasValue) { ticket.ExpenseYear=ticket.PurchaseDate.Value.Year; ticket.ExpenseMonth=ticket.PurchaseDate.Value.Month; }
                if (!string.IsNullOrWhiteSpace(ticket.CompanyName) && string.IsNullOrWhiteSpace(ticket.CompanySlug)) ticket.CompanySlug = ToSlug(ticket.CompanyName);
                ticket.AnalysisJson = jsonExtract;

                if (ticket.ExpenseYear.HasValue && ticket.ExpenseMonth.HasValue && !string.IsNullOrWhiteSpace(ticket.RelativePath))
                {
                    var isPlaceholder = ticket.RelativePath.Contains("/0000/00/");
                    var (dirRel, newFileName) = pathStrategy.Generate(ticket.CompanySlug ?? "default", ticket.ExpenseYear.Value, ticket.ExpenseMonth.Value, ticket.PublicId.ToString("N"), ticket.FileName ?? Path.GetFileName(filePath!));
                    var targetRel = Path.Combine(dirRel, newFileName).Replace(Path.DirectorySeparatorChar,'/');
                    if (isPlaceholder || !ticket.RelativePath.Equals(targetRel, StringComparison.OrdinalIgnoreCase))
                    {
                        var uploadsRoot = Path.Combine(env.ContentRootPath, "Uploads");
                        var currentPhysical = storage.GetPhysicalPath(ticket.RelativePath);
                        var newPhysical = Path.Combine(uploadsRoot, dirRel, newFileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(newPhysical)!);
                        if (!Path.GetFullPath(currentPhysical).Equals(Path.GetFullPath(newPhysical), StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(newPhysical)) File.Delete(newPhysical);
                            File.Move(currentPhysical, newPhysical);
                            ticket.RelativePath = targetRel; ticket.FileUrl = $"/Uploads/{targetRel}";
                            _logger.LogInformation("[Processor] Ticket {Id} movido a {Path}", ticket.Id, targetRel);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(markdownPart))
                {
                    var analysisFileName = ticket.PublicId + "-analysis.md";
                    string analysisRelPath; string analysisPath;
                    if (!string.IsNullOrWhiteSpace(ticket.RelativePath)) { var dir = Path.GetDirectoryName(ticket.RelativePath); analysisRelPath = Path.Combine(dir!, analysisFileName).Replace('\\','/'); analysisPath = storage.GetPhysicalPath(analysisRelPath); }
                    else { var uploadsDir = Path.Combine(env.ContentRootPath, "Uploads"); analysisPath = Path.Combine(uploadsDir, analysisFileName); analysisRelPath = analysisFileName; }
                    Directory.CreateDirectory(Path.GetDirectoryName(analysisPath)!);
                    await File.WriteAllTextAsync(analysisPath, markdownPart, System.Text.Encoding.UTF8, ct);
                    ticket.AnalysisMarkdown = markdownPart; ticket.AnalysisFileName = analysisFileName; ticket.AnalysisFileUrl = $"/Uploads/{analysisRelPath}";
                }

                ticket.Status = "Completed";
                log.Amount = ticket.Amount; log.Company = ticket.CompanyName; log.Category = ticket.Category; log.Success = true;
            }
            catch (Exception ex)
            { ticket.Status="Error"; ticket.LastErrorMessage=$"Error parse JSON: {ex.Message}"; log.Success=false; log.ErrorMessage="JSON parse fail"; _logger.LogWarning(ex, "[Processor] JSON parse fail Ticket {Id}", ticket.Id); return false; }
            return true;
        }
        catch (Exception ex)
        {
            ticket.Status = "Error"; ticket.LastErrorMessage = $"Excepción: {ex.Message}"; log.Success=false; log.ErrorMessage=ex.Message; _logger.LogError(ex, "[Processor] Excepcion ticket {Id}", ticketId); return false;
        }
        finally
        {
            sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds;
            try { db.AnalysisLogs.Add(log); await db.SaveChangesAsync(ct); } catch (Exception ex) { _logger.LogWarning(ex, "[Processor] No se pudo guardar log final ticket {Id}", ticketId); }
        }
    }

    private async Task<byte[]> OptimizeImageAsync(string path, CancellationToken ct)
    {
        const int maxW = 1600, maxH = 1600, maxBytes = 4 * 1024 * 1024;
        using var img = await Image.LoadAsync(path, ct);
        if (img.Width > maxW || img.Height > maxH)
        { var ratio = Math.Min((double)maxW/img.Width, (double)maxH/img.Height); img.Mutate(x => x.Resize((int)(img.Width*ratio), (int)(img.Height*ratio))); }
        using var ms = new MemoryStream(); var enc = new JpegEncoder { Quality = 85 }; await img.SaveAsJpegAsync(ms, enc, ct); var bytes = ms.ToArray();
        if (bytes.Length > maxBytes) { ms.SetLength(0); enc = new JpegEncoder { Quality = 75 }; await img.SaveAsJpegAsync(ms, enc, ct); bytes = ms.ToArray(); }
        return bytes;
    }

    private static string? ExtractJson(string text)
    { int f = text.IndexOf('{'); int l = text.LastIndexOf('}'); if (f>=0 && l>f) { var c = text.Substring(f, l-f+1).Trim(); if (c.StartsWith("{") && c.EndsWith("}")) return c; } return null; }

    private static DateTime? ExtractDate(System.Text.Json.JsonElement root)
    {
        string[] fields = { "date","ticketDate","fecha","Date","TicketDate","Fecha","transactionDate","TransactionDate","PurchaseDate","purchaseDate" };
        foreach (var f in fields)
        { if (root.TryGetProperty(f, out var d) && d.ValueKind==System.Text.Json.JsonValueKind.String) { var s = d.GetString(); if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out var dt)) return dt; } }
        if (root.TryGetProperty("Summary", out var summary)) foreach (var f in fields) if (summary.TryGetProperty(f, out var d2) && d2.ValueKind==System.Text.Json.JsonValueKind.String) { var s2 = d2.GetString(); if (!string.IsNullOrWhiteSpace(s2) && DateTime.TryParse(s2, out var dt2)) return dt2; }
        return null;
    }

    private static string ToSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "default";
        text = text.ToLowerInvariant();
        text = System.Text.RegularExpressions.Regex.Replace(text.Normalize(System.Text.NormalizationForm.FormD), @"[\p{Mn}]", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^a-z0-9\s-]", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        text = text.Replace(' ', '-');
        text = System.Text.RegularExpressions.Regex.Replace(text, @"-+", "-");
        text = text.Trim('-');
        if (text.Length > 50) text = text[..50].TrimEnd('-');
        return string.IsNullOrEmpty(text) ? "default" : text;
    }
}
