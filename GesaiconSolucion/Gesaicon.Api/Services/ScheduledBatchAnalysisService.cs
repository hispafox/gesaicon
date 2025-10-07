using Gesaicon.Api.Data;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Gesaicon.Api.Services
{
    public class ScheduledBatchAnalysisService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly IConfiguration _config;
        private readonly ILogger<ScheduledBatchAnalysisService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAnalysisPromptProvider _promptProvider;

        public ScheduledBatchAnalysisService(IServiceProvider sp, IConfiguration config, ILogger<ScheduledBatchAnalysisService> logger, IHttpClientFactory httpClientFactory, IAnalysisPromptProvider promptProvider)
        {
            _sp = sp; _config = config; _logger = logger; _httpClientFactory = httpClientFactory; _promptProvider = promptProvider;
            _logger.LogInformation("[ScheduledBatch] Servicio inicializado");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[ScheduledBatch] Worker iniciado");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bool enabled = _config.GetValue<bool?>("Analysis:Background:Enabled") ?? false;
                    _logger.LogInformation("[ScheduledBatch] Enabled={Enabled}", enabled);
                    if (enabled)
                    {
                        int interval = _config.GetValue<int?>("Analysis:Background:IntervalMinutes") ?? 60;
                        int maxPerRun = _config.GetValue<int?>("Analysis:Background:MaxPerRun") ?? 20;
                        _logger.LogInformation("[ScheduledBatch] Intervalo configurado: {Interval} minutos", interval);
                        await RunBatchAsync(maxPerRun, stoppingToken);
                        await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
                    }
                    else
                    {
                        _logger.LogInformation("[ScheduledBatch] Batch deshabilitado, re-chequeando en 5 min");
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // re-chequear cada 5 min
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ScheduledBatch] Error ciclo principal");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }

        private async Task RunBatchAsync(int maxPerRun, CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
            var model = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
            var apiKey = _config["GROQ:ApiKey"]; if (string.IsNullOrWhiteSpace(apiKey)) { _logger.LogWarning("[ScheduledBatch] ApiKey no configurada"); return; }

            var tickets = await db.ExpenseTickets
                .Where(t => (t.Status == "PendingAnalysis") || (t.Status == "Error" && t.RetryCount < 3))
                .OrderBy(t => t.UploadedAt)
                .Take(maxPerRun)
                .ToListAsync(ct);
            if (!tickets.Any()) return;

            _logger.LogInformation("[ScheduledBatch] Procesando {Count} tickets (incluyendo reintentos)", tickets.Count);

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            int maxDegree = _config.GetValue<int?>("Analysis:Background:MaxDegree") ?? 3;
            var semaphore = new SemaphoreSlim(maxDegree);
            var tasks = tickets.Select(async ticket =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (ticket.Status == "Error") ticket.Status = "PendingAnalysis"; // volver a estado pendiente
                    ticket.RetryCount += 1; // incrementar intento
                    await db.SaveChangesAsync(ct);
                    await ProcessTicketAsync(ticket, db, client, endpoint, model, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks);
        }


        // TODO: Revisar que funciona
        private async Task ProcessTicketAsync(Gesaicon.Api.Models.ExpenseTicket ticket, GesaiconDbContext db, HttpClient client, string endpoint, string model, CancellationToken ct)
        {
            using var storageScope = _sp.CreateScope();
            var storage = storageScope.ServiceProvider.GetRequiredService<IFileStorageService>();
            var pathStrategy = storageScope.ServiceProvider.GetRequiredService<IBusinessFilePathStrategy>();
            var env = storageScope.ServiceProvider.GetRequiredService<IHostEnvironment>();
            
            // Usar RelativePath con fallback a estructura legacy
            string? filePath = null;
            if (!string.IsNullOrWhiteSpace(ticket.RelativePath))
            {
                filePath = storage.GetPhysicalPath(ticket.RelativePath);
            }
            else if (!string.IsNullOrWhiteSpace(ticket.FileName))
            {
                // Fallback para tickets legacy (estructura antigua)
                var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
                filePath = Path.Combine(uploadsDir, ticket.FileName);
            }
            
            _logger.LogInformation("[ScheduledBatch] filePath={FilePath} relPath={RelPath}", filePath, ticket.RelativePath);
            if (filePath == null || !File.Exists(filePath)) 
            { 
                ticket.Status = "Error";
                ticket.LastErrorMessage = "Archivo no encontrado";
                await db.SaveChangesAsync(ct); 
                return; 
            }

            byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
            string mime = GetMimeFromExtension(Path.GetExtension(filePath));
            string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

            var prompt = _promptProvider.GetPrompt();
            var payload = new
            {
                model,
                messages = new object[]{ new { role = "user", content = new object[]{ new { type = "text", text = prompt }, new { type = "image_url", image_url = new { url = dataUrl } } } } },
                temperature = 0,
                max_completion_tokens = 500
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);

            var log = new Gesaicon.Api.Models.AnalysisLog
            {
                TicketId = ticket.Id,
                StartedAt = DateTime.UtcNow,
                Operation = "ScheduledBatch",
                Attempt = ticket.RetryCount,
                Model = model,
                Endpoint = endpoint,
                FileHashSnapshot = ticket.FileHash
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(endpoint, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    log.Success = false; log.ErrorMessage = $"HTTP {(int)resp.StatusCode}"; 
                    ticket.Status = "Error";
                    ticket.LastErrorMessage = $"Error HTTP {(int)resp.StatusCode}";
                }
                else
                {
                    string? rawText = null;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        rawText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        if (doc.RootElement.TryGetProperty("usage", out var usageEl))
                        {
                            log.JsonUsageRaw = usageEl.GetRawText();
                            if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind==System.Text.Json.JsonValueKind.Number) log.PromptTokens = pt.GetInt32();
                            if (usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind==System.Text.Json.JsonValueKind.Number) log.CompletionTokens = ctEl.GetInt32();
                            if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind==System.Text.Json.JsonValueKind.Number) log.TotalTokens = tt.GetInt32();
                        }
                    }
                    catch { rawText = body; }

                    string? markdownPart = null; string? jsonPart = rawText;
                    if (!string.IsNullOrWhiteSpace(rawText))
                    {
                        var splitIndex = rawText.IndexOf("}\n\n"); if (splitIndex < 0) splitIndex = rawText.IndexOf("}\r\n\r\n");
                        if (splitIndex > 0) { jsonPart = rawText.Substring(0, splitIndex + 1).Trim(); markdownPart = rawText.Substring(splitIndex + 3).Trim(); }
                    }

                    var jsonExtract = ExtractJson(jsonPart ?? string.Empty);
                    decimal? amount = null; string? company = null; string? category = null;
                    DateTime? ticketDate = null;
                    
                    if (jsonExtract != null)
                    {
                        try
                        {
                            using var parsed = System.Text.Json.JsonDocument.Parse(jsonExtract);
                            if (parsed.RootElement.TryGetProperty("Amount", out var aEl))
                            {
                                if (aEl.ValueKind == System.Text.Json.JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) amount = dec;
                                else if (decimal.TryParse(aEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec2)) amount = dec2;
                            }
                            if (parsed.RootElement.TryGetProperty("Company", out var cEl)) company = cEl.GetString();
                            if (parsed.RootElement.TryGetProperty("Category", out var catEl)) category = catEl.GetString();
                            
                            // NUEVO: Extraer fecha del ticket desde el JSON del análisis
                            string[] dateFields = { "date", "ticketDate", "fecha", "Date", "TicketDate", "Fecha", "transactionDate" };
                            foreach (var field in dateFields)
                            {
                                if (parsed.RootElement.TryGetProperty(field, out var dateEl))
                                {
                                    var dateStr = dateEl.GetString();
                                    if (!string.IsNullOrWhiteSpace(dateStr) && DateTime.TryParse(dateStr, out var parsedDate))
                                    {
                                        ticketDate = parsedDate;
                                        _logger.LogInformation("[ScheduledBatch] Ticket {Id}: fecha extraída del análisis: {Date}", ticket.Id, ticketDate.Value.ToString("yyyy-MM-dd"));
                                        break;
                                    }
                                }
                            }
                            
                            log.Amount = amount; log.Company = company; log.Category = category;
                        }
                        catch { log.ErrorMessage = "JSON parse fail"; }
                    }
                    else { log.ErrorMessage = "No JSON fragment"; }

                    if (ticketDate.HasValue)
                    {
                        ticket.PurchaseDate = ticketDate.Value;
                        ticket.ExpenseYear = ticketDate.Value.Year;
                        ticket.ExpenseMonth = ticketDate.Value.Month;
                    }

                    string? combinedJson = null; 
                    try 
                    { 
                        combinedJson = System.Text.Json.JsonSerializer.Serialize(new 
                        { 
                            Summary = new 
                            { 
                                Amount = amount, 
                                Company = company, 
                                Category = category,
                                Date = ticketDate?.ToString("yyyy-MM-dd")
                            }, 
                            Model = model, 
                            FileName = ticket.FileName, 
                            Source = "ScheduledBatchAnalysisService" 
                        }); 
                    } 
                    catch {}

                    // Reubicación: SOLO si ya conocemos fecha real (ExpenseYear/Month no nulos) y (a) path está en placeholder 0000/00 o (b) difiere de año/mes actuales
                    if (ticket.ExpenseYear.HasValue && ticket.ExpenseMonth.HasValue && !string.IsNullOrWhiteSpace(ticket.RelativePath))
                    {
                        var currentParts = ticket.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        bool isPlaceholder = currentParts.Length >= 4 && currentParts[^3] == ticket.ExpenseYear?.ToString("0000") && currentParts[^2] == ticket.ExpenseMonth?.ToString("00") ? false : ticket.RelativePath.Contains("/0000/00/");
                        // Generar ruta objetivo con pathStrategy
                        var (dirRel, fileName) = pathStrategy.Generate(ticket.CompanySlug ?? "default", ticket.ExpenseYear.Value, ticket.ExpenseMonth.Value, ticket.PublicId.ToString("N"), ticket.FileName ?? Path.GetFileName(filePath));
                        var targetRel = Path.Combine(dirRel, fileName).Replace(Path.DirectorySeparatorChar, '/');
                        if (isPlaceholder || !ticket.RelativePath.Equals(targetRel, StringComparison.OrdinalIgnoreCase))
                        {
                            var uploadsRoot = Path.Combine(env.ContentRootPath, "Uploads");
                            var currentPhysical = storage.GetPhysicalPath(ticket.RelativePath);
                            var newPhysical = Path.Combine(uploadsRoot, dirRel, fileName);
                            if (!Directory.Exists(Path.GetDirectoryName(newPhysical)!)) Directory.CreateDirectory(Path.GetDirectoryName(newPhysical)!);
                            if (!Path.GetFullPath(currentPhysical).Equals(Path.GetFullPath(newPhysical), StringComparison.OrdinalIgnoreCase))
                            {
                                if (File.Exists(newPhysical)) File.Delete(newPhysical); // reemplazar (placeholder)
                                File.Move(currentPhysical, newPhysical);
                                ticket.RelativePath = targetRel;
                                ticket.FileUrl = $"/Uploads/{targetRel}";
                                _logger.LogInformation("[ScheduledBatch] Ticket {Id} movido a ruta definitiva {Path}", ticket.Id, targetRel);
                            }
                        }
                    }

                    string? analysisFileName = null; string? analysisFileUrl = null;
                    if (!string.IsNullOrWhiteSpace(markdownPart))
                    {
                        analysisFileName = ticket.PublicId + "-analysis.md";
                        
                        // Guardar en la misma carpeta que el ticket
                        string analysisPath;
                        string analysisRelPath;
                        if (!string.IsNullOrWhiteSpace(ticket.RelativePath))
                        {
                            var dir = Path.GetDirectoryName(ticket.RelativePath);
                            analysisRelPath = Path.Combine(dir!, analysisFileName).Replace('\\', '/');
                            analysisPath = storage.GetPhysicalPath(analysisRelPath);
                        }
                        else
                        {
                            // Fallback legacy
                            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
                            analysisPath = Path.Combine(uploadsDir, analysisFileName);
                            analysisRelPath = analysisFileName;
                        }
                        
                        Directory.CreateDirectory(Path.GetDirectoryName(analysisPath)!);
                        await File.WriteAllTextAsync(analysisPath, markdownPart, System.Text.Encoding.UTF8, ct);
                        analysisFileUrl = $"/Uploads/{analysisRelPath}";
                    }

                    if (amount.HasValue) ticket.Amount = amount;
                    if (!string.IsNullOrWhiteSpace(company)) ticket.CompanyName = company;
                    if (!string.IsNullOrWhiteSpace(category)) ticket.Category = category;
                    ticket.AnalysisJson = combinedJson ?? jsonExtract;
                    ticket.AnalysisMarkdown = markdownPart;
                    ticket.AnalysisFileName = analysisFileName;
                    ticket.AnalysisFileUrl = analysisFileUrl;
                    ticket.Status = log.ErrorMessage == null ? "Completed" : "Error";
                    if (log.ErrorMessage != null) ticket.LastErrorMessage = log.ErrorMessage;
                    log.Success = log.ErrorMessage == null;
                }
            }
            catch (Exception ex)
            {
                log.Success = false; log.ErrorMessage = ex.Message; ticket.Status = "Error"; ticket.LastErrorMessage = $"Excepción: {ex.Message}"; _logger.LogError(ex, "[ScheduledBatch] Excepcion ticket {Id}", ticket.Id);
            }
            finally
            {
                sw.Stop(); log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds;
                try { await db.SaveChangesAsync(ct); db.AnalysisLogs.Add(log); await db.SaveChangesAsync(ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[ScheduledBatch] No se pudo guardar AnalysisLog"); }
            }
        }

        private static string GetMimeFromExtension(string ext) => ext.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        private static string? ExtractJson(string text)
        {
            int first = text.IndexOf('{');
            int last = text.LastIndexOf('}');
            if (first >= 0 && last > first)
            {
                var candidate = text.Substring(first, last - first + 1).Trim();
                if (candidate.StartsWith("{") && candidate.EndsWith("}")) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Convierte un nombre de empresa a slug válido para rutas de archivo.
        /// </summary>
        private static string ToSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "unknown";

            text = text.ToLowerInvariant();
            text = System.Text.RegularExpressions.Regex.Replace(
                text.Normalize(System.Text.NormalizationForm.FormD),
                @"[\p{Mn}]",
                string.Empty
            );
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[^a-z0-9\s-]", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            text = text.Replace(' ', '-');
            text = System.Text.RegularExpressions.Regex.Replace(text, @"-+", "-");
            text = text.Trim('-');
            if (text.Length > 50)
                text = text.Substring(0, 50).TrimEnd('-');
            return string.IsNullOrEmpty(text) ? "unknown" : text;
        }
    }
}
