using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using Gesaicon.Api.Models;
using SixLabors.ImageSharp; // para redimensionar
using SixLabors.ImageSharp.Processing;
using Microsoft.EntityFrameworkCore; // agregado para ToListAsync
using System.Security.Cryptography;
using Gesaicon.Api.Services; // prompt provider

namespace Gesaicon.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReceiptAnalysisController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<ReceiptAnalysisController> _logger;
        private readonly IAnalysisPromptProvider _promptProvider;

        public ReceiptAnalysisController(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ReceiptAnalysisController> logger, IAnalysisPromptProvider promptProvider)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
            _promptProvider = promptProvider;
        }

        private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase){ ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        private const long MaxSizeBytes = 5 * 1024 * 1024; // 5MB

        private static async Task<(byte[] bytes,string ext,string hash)> ReadAndHashAsync(IFormFile file)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var data = ms.ToArray();
            string ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(data));
            return (data, ext, hash);
        }

        [HttpPost("analyze")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Analyze([FromForm] UploadExpenseTicketRequest request, [FromQuery] int? ticketId = null)
        {
            if (request.File == null || request.File.Length == 0) return BadRequest("Archivo requerido");
            if (request.File.Length > MaxSizeBytes) return BadRequest("Archivo demasiado grande (max 5MB)");
            var extIn = Path.GetExtension(request.File.FileName);
            if (!string.IsNullOrEmpty(extIn) && !AllowedExt.Contains(extIn)) return BadRequest("Tipo de archivo no soportado");

            var apiKey = _config["GROQ:ApiKey"]; if (string.IsNullOrWhiteSpace(apiKey)) return StatusCode(500, "GROQ ApiKey no configurada");

            // Leer + hash antes de escribir en disco
            var (data, ext, hash) = await ReadAndHashAsync(request.File);

            var uploads = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            if (!Directory.Exists(uploads)) Directory.CreateDirectory(uploads);
            Guid publicId = Guid.NewGuid();
            string fileName = publicId + ext;
            string filePath = Path.Combine(uploads, fileName);

            ExpenseTicket? newTicket = null;
            if (!ticketId.HasValue)
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Gesaicon.Api.Data.GesaiconDbContext>();
                var dup = await db.ExpenseTickets.AsNoTracking().FirstOrDefaultAsync(t => t.FileHash == hash);
                if (dup != null)
                {
                    return Conflict(new { duplicateOf = dup.Id, existing = dup });
                }
                await System.IO.File.WriteAllBytesAsync(filePath, data);
                newTicket = new ExpenseTicket
                {
                    PublicId = publicId,
                    FileName = fileName,
                    FileUrl = $"/Uploads/{fileName}",
                    FileSizeBytes = data.Length,
                    FileHash = hash,
                    UploadedAt = DateTime.UtcNow,
                    Status = "Processing"
                };
                db.ExpenseTickets.Add(newTicket);
                await db.SaveChangesAsync();
                ticketId = newTicket.Id;
            }
            else
            {
                await System.IO.File.WriteAllBytesAsync(filePath, data);
            }

            var scheme = HttpContext.Request.Scheme; var host = HttpContext.Request.Host.Value; var publicUrl = $"{scheme}://{host}/Uploads/{fileName}";
            bool useDataUrl = host.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            string imageReference;
            if (useDataUrl)
            {
                await using var imgStream = new MemoryStream(data);
                using var image = await Image.LoadAsync(imgStream);
                const int maxDim = 1600;
                if (image.Width > maxDim || image.Height > maxDim)
                    image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxDim, maxDim) }));
                await using var ms = new MemoryStream();
                if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) await image.SaveAsPngAsync(ms); else await image.SaveAsJpegAsync(ms);
                var b64 = Convert.ToBase64String(ms.ToArray());
                var mime = ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                imageReference = $"data:{mime};base64,{b64}";
            }
            else imageReference = publicUrl;

            var model = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
            var prompt = _promptProvider.GetPrompt();

            var payload = new { model, messages = new object[]{ new { role = "user", content = new object[]{ new { type = "text", text = prompt }, new { type = "image_url", image_url = new { url = imageReference } } } } }, temperature = 0, max_completion_tokens = 500 };

            var client = _httpClientFactory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
            var payloadJson = JsonSerializer.Serialize(payload);

            var log = new AnalysisLog { TicketId = ticketId, StartedAt = DateTime.UtcNow, Operation = "Single", Attempt = 1, Model = model, Endpoint = endpoint, FileHashSnapshot = hash };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(endpoint, httpContent); var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                log.Success = false; log.ErrorMessage = $"HTTP {(int)response.StatusCode}"; sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds; await SaveAnalysisLogAsync(log);
                if (newTicket != null)
                {
                    using var scopeErr = HttpContext.RequestServices.CreateScope(); var dbErr = scopeErr.ServiceProvider.GetRequiredService<Gesaicon.Api.Data.GesaiconDbContext>();
                    var t = await dbErr.ExpenseTickets.FindAsync(newTicket.Id); if (t != null) { t.Status = "Error"; await dbErr.SaveChangesAsync(); }
                }
                return StatusCode((int)response.StatusCode, new { error = body });
            }

            string? rawText = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                rawText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (doc.RootElement.TryGetProperty("usage", out var usageEl))
                {
                    log.JsonUsageRaw = usageEl.GetRawText();
                    if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind==JsonValueKind.Number) log.PromptTokens = pt.GetInt32();
                    if (usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind==JsonValueKind.Number) log.CompletionTokens = ctEl.GetInt32();
                    if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind==JsonValueKind.Number) log.TotalTokens = tt.GetInt32();
                }
            }
            catch { rawText = body; }

            string? markdownPart = null; string? jsonPart = rawText;
            if (!string.IsNullOrWhiteSpace(rawText))
            {
                var splitIndex = rawText.IndexOf("}\n\n"); if (splitIndex < 0) splitIndex = rawText.IndexOf("}\r\n\r\n");
                if (splitIndex > 0) { jsonPart = rawText.Substring(0, splitIndex + 1).Trim(); markdownPart = rawText.Substring(splitIndex + 3).Trim(); }
            }

            var jsonFragment = ExtractJson(jsonPart ?? string.Empty);
            decimal? amount = null; string? company = null; string? category = null;
            if (jsonFragment != null)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(jsonFragment);
                    if (parsed.RootElement.TryGetProperty("Amount", out var aEl))
                    {
                        if (aEl.ValueKind == JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) amount = dec;
                        else if (decimal.TryParse(aEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec2)) amount = dec2;
                    }
                    if (parsed.RootElement.TryGetProperty("Company", out var cEl)) company = cEl.GetString();
                    if (parsed.RootElement.TryGetProperty("Category", out var catEl)) category = catEl.GetString();
                    log.Amount = amount; log.Company = company; log.Category = category;
                }
                catch { log.ErrorMessage = "JSON parse fail"; }
            }
            else { log.ErrorMessage = "No JSON fragment"; }

            string? combinedJson = null; try { combinedJson = JsonSerializer.Serialize(new { Summary = new { Amount = amount, Company = company, Category = category }, Model = model, FileName = fileName, Source = "ReceiptAnalysisController.Analyze" }); } catch {}

            string? analysisFileName = null; string? analysisFileUrl = null;
            if (!string.IsNullOrWhiteSpace(markdownPart))
            {
                analysisFileName = publicId + "-analysis.md"; var analysisPath = Path.Combine(uploads, analysisFileName);
                await System.IO.File.WriteAllTextAsync(analysisPath, markdownPart, Encoding.UTF8); analysisFileUrl = $"/Uploads/{analysisFileName}";
            }

            if (newTicket != null)
            {
                using var scopeUpd = HttpContext.RequestServices.CreateScope(); var dbUpd = scopeUpd.ServiceProvider.GetRequiredService<Gesaicon.Api.Data.GesaiconDbContext>();
                var ticket = await dbUpd.ExpenseTickets.FindAsync(ticketId!.Value);
                if (ticket != null)
                {
                    if (amount.HasValue) ticket.Amount = amount;
                    if (!string.IsNullOrWhiteSpace(company)) ticket.CompanyName = company;
                    if (!string.IsNullOrWhiteSpace(category)) ticket.Category = category;
                    ticket.AnalysisJson = combinedJson ?? jsonFragment; ticket.AnalysisMarkdown = markdownPart; ticket.AnalysisFileName = analysisFileName; ticket.AnalysisFileUrl = analysisFileUrl;
                    ticket.Status = log.ErrorMessage == null ? "Completed" : "Error"; await dbUpd.SaveChangesAsync(); publicId = ticket.PublicId;
                }
            }

            log.Success = log.ErrorMessage == null; sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds; await SaveAnalysisLogAsync(log);
            return Ok(new { TicketId = ticketId, PublicId = publicId, Amount = amount, Company = company, Category = category, AnalysisJson = combinedJson ?? jsonFragment, AnalysisMarkdown = markdownPart, AnalysisFileName = analysisFileName, AnalysisFileUrl = analysisFileUrl, Raw = rawText, Model = model });
        }

        private static string? ExtractJson(string text)
        {
            int first = text.IndexOf('{'); int last = text.LastIndexOf('}');
            if (first >= 0 && last > first) { var candidate = text.Substring(first, last - first + 1).Trim(); if (candidate.StartsWith("{") && candidate.EndsWith("}")) return candidate; }
            return null;
        }

        [HttpPost("analyze/advanced")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(ReceiptAnalysisResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> AnalyzeAdvanced([FromForm] ReceiptAnalysisRequest request, CancellationToken ct)
        {
            if (request.File == null || request.File.Length == 0) return BadRequest("Archivo requerido");
            if (request.File.Length > MaxSizeBytes) return BadRequest("Archivo demasiado grande (max 5MB)");
            var extIn = Path.GetExtension(request.File.FileName); if (!string.IsNullOrEmpty(extIn) && !AllowedExt.Contains(extIn)) return BadRequest("Tipo de archivo no soportado");

            _logger.LogInformation("[AnalyzeAdvanced] Inicio análisis avanzado. nombreOriginal={Name} size={Size} stream={Stream} jsonMode={JsonMode}", request.File.FileName, request.File.Length, request.Stream, request.JsonMode);
            var apiKey = _config["GROQ:ApiKey"]; if (string.IsNullOrWhiteSpace(apiKey)) return StatusCode(500, "GROQ ApiKey no configurada");

            var (data, ext, hash) = await ReadAndHashAsync(request.File);
            using var scopeDup = HttpContext.RequestServices.CreateScope();
            var dbDup = scopeDup.ServiceProvider.GetRequiredService<Gesaicon.Api.Data.GesaiconDbContext>();
            var dup = await dbDup.ExpenseTickets.AsNoTracking().FirstOrDefaultAsync(t => t.FileHash == hash, ct);
            if (dup != null)
            {
                return Conflict(new { duplicateOf = dup.Id, existing = dup });
            }

            var uploads = Path.Combine(Directory.GetCurrentDirectory(), "Uploads"); if (!Directory.Exists(uploads)) Directory.CreateDirectory(uploads);
            var fileName = Guid.NewGuid() + ext; var filePath = Path.Combine(uploads, fileName); await System.IO.File.WriteAllBytesAsync(filePath, data, ct);
            _logger.LogInformation("[AnalyzeAdvanced] Archivo guardado en {Path}", filePath);

            var scheme = HttpContext.Request.Scheme; var host = HttpContext.Request.Host.Value; var publicUrl = $"{scheme}://{host}/Uploads/{fileName}"; bool useDataUrl = host.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            string? imageReference;
            if (useDataUrl)
            {
                _logger.LogDebug("[AnalyzeAdvanced] Usando data URL (localhost) para imagen");
                await using var imgStream2 = new MemoryStream(data);
                using var image2 = await Image.LoadAsync(imgStream2, ct);
                const int maxDim2 = 1600;
                if (image2.Width > maxDim2 || image2.Height > maxDim2)
                {
                    _logger.LogDebug("[AnalyzeAdvanced] Redimensionando imagen de {W}x{H} (max {Max})", image2.Width, image2.Height, maxDim2);
                    image2.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxDim2, maxDim2) }));
                }
                await using var ms2 = new MemoryStream();
                if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) await image2.SaveAsPngAsync(ms2, ct); else await image2.SaveAsJpegAsync(ms2, ct);
                var b642 = Convert.ToBase64String(ms2.ToArray()); var mime2 = ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg"; imageReference = $"data:{mime2};base64,{b642}";
                _logger.LogDebug("[AnalyzeAdvanced] Imagen convertida a base64 tamaño={Len} chars", b642.Length);
            }
            else { _logger.LogDebug("[AnalyzeAdvanced] Usando URL pública {Url}", publicUrl); imageReference = publicUrl; }

            var model = request.Model ?? _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
            var basePrompt = _promptProvider.GetPrompt(); // reutilizamos el mismo prompt completo
            if (!string.IsNullOrWhiteSpace(request.ExtraPrompt)) basePrompt += " " + request.ExtraPrompt;

            var payload = new { model, messages = new object[]{ new { role = "user", content = new object[]{ new { type = "text", text = basePrompt }, new { type = "image_url", image_url = new { url = imageReference } } } } }, temperature = request.Temperature ?? 0m, max_completion_tokens = request.MaxTokens ?? 500, top_p = request.TopP ?? 1m, stream = request.Stream, response_format = request.JsonMode ? new { type = "json_object" } : null };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions{ DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions"; _logger.LogDebug("[AnalyzeAdvanced] Payload length={Len} chars", json.Length);

            var client = _httpClientFactory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var log = new AnalysisLog { TicketId = null, StartedAt = DateTime.UtcNow, Operation = request.Stream ? "AdvancedStream" : "Advanced", Attempt = 1, Model = model, Endpoint = endpoint, FileHashSnapshot = hash };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (request.Stream)
            {
                var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") }; httpReq.Headers.Accept.ParseAdd("text/event-stream");
                var httpResp = await client.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct); _logger.LogInformation("[AnalyzeAdvanced] Respuesta streaming status={Status}", httpResp.StatusCode);
                if (!httpResp.IsSuccessStatusCode)
                { log.Success = false; log.ErrorMessage = $"HTTP {(int)httpResp.StatusCode}"; sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds; await SaveAnalysisLogAsync(log); var errBody = await httpResp.Content.ReadAsStringAsync(ct); return StatusCode((int)httpResp.StatusCode, new { error = errBody }); }
                var stream = await httpResp.Content.ReadAsStreamAsync(ct); using var reader = new StreamReader(stream); var sb = new StringBuilder();
                while (!reader.EndOfStream)
                { var line = await reader.ReadLineAsync(); if (line == null) break; if (!line.StartsWith("data: ")) continue; var payloadLine = line[6..].Trim(); if (payloadLine == "[DONE]") break; try { using var doc = JsonDocument.Parse(payloadLine); var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta"); if (delta.TryGetProperty("content", out var cEl)) sb.Append(cEl.GetString()); } catch { } }
                var full = sb.ToString(); _logger.LogDebug("[AnalyzeAdvanced] Texto acumulado length={Len}", full.Length);
                var jsonFragment = ExtractJson(full); decimal? amount = null; string? company = null; string? category = null; if (jsonFragment != null) { try { using var parsed = JsonDocument.Parse(jsonFragment); if (parsed.RootElement.TryGetProperty("Amount", out var aEl) && aEl.ValueKind == JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) amount = dec; if (parsed.RootElement.TryGetProperty("Company", out var cEl)) company = cEl.GetString(); if (parsed.RootElement.TryGetProperty("Category", out var catEl)) category = catEl.GetString(); } catch {} }
                log.Success = true; sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds; await SaveAnalysisLogAsync(log);
                return Ok(new ReceiptAnalysisResult { Amount = amount, Company = company, Category = category, Raw = full, Model = model });
            }
            else
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json"); var response = await client.PostAsync(endpoint, content, ct); var body = await response.Content.ReadAsStringAsync(ct); _logger.LogInformation("[AnalyzeAdvanced] Respuesta status={Status} length={Len} chars", response.StatusCode, body.Length);
                if (!response.IsSuccessStatusCode && body.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) && model != "meta-llama/llama-4-scout-17b-16e-instruct")
                {
                    _logger.LogWarning("[AnalyzeAdvanced] Modelo {OldModel} no encontrado. Reintentando con fallback", model);
                    model = "meta-llama/llama-4-scout-17b-16e-instruct";
                    var retryPayload = new { model, messages = ((object[])payload.messages), temperature = request.Temperature ?? 0m, max_completion_tokens = request.MaxTokens ?? 500, top_p = request.TopP ?? 1m, stream = false };
                    var retryJson = JsonSerializer.Serialize(retryPayload); using var retryContent = new StringContent(retryJson, Encoding.UTF8, "application/json"); response = await client.PostAsync(endpoint, retryContent, ct); body = await response.Content.ReadAsStringAsync(ct); _logger.LogInformation("[AnalyzeAdvanced] Respuesta reintento status={Status} length={Len} chars", response.StatusCode, body.Length);
                }
                if (!response.IsSuccessStatusCode)
                { log.Success = false; log.ErrorMessage = $"HTTP {(int)response.StatusCode}"; sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds; await SaveAnalysisLogAsync(log); return StatusCode((int)response.StatusCode, new { error = body }); }
                string? rawText = null; try { using var doc = JsonDocument.Parse(body); rawText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(); if (doc.RootElement.TryGetProperty("usage", out var usageEl)) { log.JsonUsageRaw = usageEl.GetRawText(); if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind==JsonValueKind.Number) log.PromptTokens = pt.GetInt32(); if (usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind==JsonValueKind.Number) log.CompletionTokens = ctEl.GetInt32(); if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind==JsonValueKind.Number) log.TotalTokens = tt.GetInt32(); } } catch { rawText = body; }
                var jsonFragment = ExtractJson(rawText ?? string.Empty); decimal? amount = null; string? company = null; string? category = null; if (jsonFragment != null) { try { using var parsed = JsonDocument.Parse(jsonFragment); if (parsed.RootElement.TryGetProperty("Amount", out var aEl) && aEl.ValueKind == JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) amount = dec; if (parsed.RootElement.TryGetProperty("Company", out var cEl)) company = cEl.GetString(); if (parsed.RootElement.TryGetProperty("Category", out var catEl)) category = catEl.GetString(); } catch {} }
                log.Success = true; sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds; await SaveAnalysisLogAsync(log); return Ok(new ReceiptAnalysisResult { Amount = amount, Company = company, Category = category, Raw = rawText, Model = model });
            }
        }

        [HttpPost("analyze/pending")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> AnalyzePending([FromQuery] int max = 10, [FromQuery] bool includeInProgress = false)
        {
            _logger.LogInformation("[AnalyzePending] Iniciando análisis batch. Max={Max} includeInProgress={IncludeInProgress}", max, includeInProgress);
            if (max <= 0) max = 10;

            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Gesaicon.Api.Data.GesaiconDbContext>();

            var query = db.ExpenseTickets
                .Where(t => t.Status == "PendingAnalysis" || (includeInProgress && t.Status == "Processing"))
                .OrderBy(t => t.UploadedAt)
                .Take(max);

            var tickets = await query.ToListAsync();
            if (!tickets.Any())
            {
                _logger.LogInformation("[AnalyzePending] No hay tickets pendientes");
                return Ok(new { processed = 0 });
            }

            var apiKey = _config["GROQ:ApiKey"]; if (string.IsNullOrWhiteSpace(apiKey)) return StatusCode(500, "GROQ ApiKey no configurada");

            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var prompt = _promptProvider.GetPrompt();
            int success = 0; int failed = 0; var results = new List<object>();
            foreach (var ticket in tickets)
            {
                var log = new AnalysisLog
                {
                    TicketId = ticket.Id,
                    StartedAt = DateTime.UtcNow,
                    Operation = "Batch",
                    Attempt = 1,
                    Model = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct",
                    Endpoint = endpoint,
                    FileHashSnapshot = ticket.FileHash
                };
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    ticket.Status = "Processing";
                    await db.SaveChangesAsync();

                    var filePath = string.IsNullOrWhiteSpace(ticket.FileUrl) ? null : Path.Combine(Directory.GetCurrentDirectory(), ticket.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    if (filePath == null || !System.IO.File.Exists(filePath))
                    {
                        _logger.LogWarning("[AnalyzePending] Archivo faltante para ticket {Id}", ticket.Id);
                        ticket.Status = "Error"; await db.SaveChangesAsync(); failed++; results.Add(new { ticket.Id, error = "Archivo no encontrado" }); continue;
                    }

                    byte[] bytes = await System.IO.File.ReadAllBytesAsync(filePath);
                    string mime = GetMimeFromExtension(Path.GetExtension(filePath));
                    string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

                    var payload = new { model = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct", messages = new object[]{ new { role = "user", content = new object[]{ new { type = "text", text = prompt }, new { type = "image_url", image_url = new { url = dataUrl } } } } }, temperature = 0, max_completion_tokens = 500 };
                    var json = JsonSerializer.Serialize(payload);
                    using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync(endpoint, httpContent);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("[AnalyzePending] Error ticket {Id} Status={Status}", ticket.Id, resp.StatusCode);
                        ticket.Status = "Error"; await db.SaveChangesAsync(); failed++; results.Add(new { ticket.Id, status = resp.StatusCode.ToString(), error = "Groq error" }); continue;
                    }

                    string? contentText = null; try { using var doc = JsonDocument.Parse(body); contentText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(); } catch { contentText = body; }

                    string? jsonPart = contentText;
                    if (!string.IsNullOrWhiteSpace(contentText))
                    {
                        var splitIndex = contentText.IndexOf("}\n\n"); if (splitIndex < 0) splitIndex = contentText.IndexOf("}\r\n\r\n");
                        if (splitIndex > 0) jsonPart = contentText.Substring(0, splitIndex + 1).Trim();
                    }
                    var jsonExtract = ExtractJson(jsonPart ?? string.Empty);
                    if (jsonExtract == null)
                    {
                        _logger.LogWarning("[AnalyzePending] No JSON parseable ticket {Id}", ticket.Id);
                        ticket.Status = "Error"; await db.SaveChangesAsync(); failed++; results.Add(new { ticket.Id, error = "Sin JSON" }); continue;
                    }

                    try
                    {
                        using var parsed = JsonDocument.Parse(jsonExtract);
                        if (parsed.RootElement.TryGetProperty("Amount", out var aEl))
                        {
                            if (aEl.ValueKind == JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) ticket.Amount = dec;
                            else if (decimal.TryParse(aEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec2)) ticket.Amount = dec2;
                        }
                        if (parsed.RootElement.TryGetProperty("Company", out var cEl)) ticket.CompanyName = cEl.GetString();
                        if (parsed.RootElement.TryGetProperty("Category", out var catEl)) ticket.Category = catEl.GetString();
                        ticket.AnalysisJson = jsonExtract; ticket.Status = "Completed"; await db.SaveChangesAsync(); success++; results.Add(new { ticket.Id, ticket.Amount, ticket.CompanyName, ticket.Category });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AnalyzePending] Error parseando JSON ticket {Id}", ticket.Id); ticket.Status = "Error"; await db.SaveChangesAsync(); failed++; results.Add(new { ticket.Id, error = "JSON parse fail" });
                    }
                }
                catch (Exception ex)
                {
                    log.Success = false; log.ErrorMessage = ex.Message; _logger.LogError(ex, "[AnalyzePending] Excepción procesando ticket {Id}", ticket.Id); ticket.Status = "Error"; try { await db.SaveChangesAsync(); } catch { } failed++; results.Add(new { ticket.Id, error = ex.Message });
                }
                finally
                {
                    stopwatch.Stop(); log.Success = true; log.FinishedAt = DateTime.UtcNow; log.DurationMs = stopwatch.ElapsedMilliseconds; await SaveAnalysisLogAsync(log);
                }
            }

            return Ok(new { processed = tickets.Count, success, failed, results });
        }

        private static string GetMimeFromExtension(string ext)
        {
            ext = ext.ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }

        private async Task SaveAnalysisLogAsync(Gesaicon.Api.Models.AnalysisLog log)
        {
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Gesaicon.Api.Data.GesaiconDbContext>();
                db.AnalysisLogs.Add(log);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar AnalysisLog");
            }
        }


        // TODO: Revisar que funciona
        [HttpPost("retry/{ticketId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Retry(int ticketId, [FromServices] Gesaicon.Api.Data.GesaiconDbContext db, [FromServices] Gesaicon.Api.Services.IReceiptAnalysisQueue queue, [FromQuery] bool enqueue = true, [FromQuery] bool force = false)
        {
            var ticket = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
            if (ticket == null) return NotFound();
            if (ticket.Status == "Completed" && !force) return BadRequest("Ya completado");
            ticket.Status = "PendingAnalysis";
            if (ticket.RetryCount < 3) ticket.RetryCount += 1; // contar el reintento manual también
            await db.SaveChangesAsync();
            if (enqueue)
            {
                await queue.EnqueueAsync(ticket.Id, ticket.RetryCount);
                return Ok(new { ticket.Id, ticket.RetryCount, enqueued = true, forced = force });
            }
            return Ok(new { ticket.Id, ticket.RetryCount, enqueued = false, forced = force, message = "Se reprocesará por batch" });
        }


        // TODO: Revisar que funciona
        [HttpPost("retry/by-guid/{publicId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> RetryByGuid(Guid publicId, [FromServices] Gesaicon.Api.Data.GesaiconDbContext db, [FromServices] Gesaicon.Api.Services.IReceiptAnalysisQueue queue, [FromQuery] bool enqueue = true)
        {
            var ticket = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.PublicId == publicId);
            if (ticket == null) return NotFound();
            if (ticket.Status == "Completed") return BadRequest("Ya completado");
            ticket.Status = "PendingAnalysis";
            if (ticket.RetryCount < 3) ticket.RetryCount += 1;
            await db.SaveChangesAsync();
            if (enqueue)
            {
                await queue.EnqueueAsync(ticket.Id, ticket.RetryCount);
                return Ok(new { ticket.Id, ticket.PublicId, ticket.RetryCount, enqueued = true });
            }
            return Ok(new { ticket.Id, ticket.PublicId, ticket.RetryCount, enqueued = false, message = "Se reprocesará por batch" });
        }
    }
}
