using System.Threading.Channels;
using Gesaicon.Api.Data;
using Microsoft.EntityFrameworkCore;
using Gesaicon.Api.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Gesaicon.Api.Services
{
    public record AnalysisWorkItem(int TicketId, int Attempt);

    public interface IReceiptAnalysisQueue
    {
        ValueTask EnqueueAsync(int ticketId, int attempt = 1);
    }

    public class ReceiptAnalysisQueueService : BackgroundService, IReceiptAnalysisQueue
    {
        private readonly Channel<AnalysisWorkItem> _channel;
        private readonly IServiceProvider _sp;
        private readonly ILogger<ReceiptAnalysisQueueService> _logger;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAnalysisPromptProvider _promptProvider;

        public ReceiptAnalysisQueueService(IServiceProvider sp, ILogger<ReceiptAnalysisQueueService> logger, IConfiguration config, IHttpClientFactory httpClientFactory, IAnalysisPromptProvider promptProvider)
        {
            _sp = sp;
            _logger = logger;
            _config = config;
            _httpClientFactory = httpClientFactory;
            _promptProvider = promptProvider;
            var capacity = _config.GetValue<int?>("Analysis:Queue:Capacity") ?? 500;
            var options = BoundedChannelOptions(capacity);
            _channel = Channel.CreateBounded<AnalysisWorkItem>(options);
        }

        private static BoundedChannelOptions BoundedChannelOptions(int capacity) => new(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        };

        public ValueTask EnqueueAsync(int ticketId, int attempt = 1)
        {
            return _channel.Writer.WriteAsync(new AnalysisWorkItem(ticketId, attempt));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[QueueService] Worker iniciado");
            while (!stoppingToken.IsCancellationRequested)
            {
                AnalysisWorkItem item;
                try
                {
                    _logger.LogDebug("[QueueService] Esperando item en el canal...");
                    item = await _channel.Reader.ReadAsync(stoppingToken);
                    _logger.LogDebug("[QueueService] Item recibido: TicketId={TicketId}, Attempt={Attempt}", item.TicketId, item.Attempt);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[QueueService] Error inesperado leyendo del canal");
                    continue;
                }
                _ = ProcessItemAsync(item, stoppingToken); // fire & forget
            }
            _logger.LogInformation("[QueueService] Worker detenido");
        }

        private async Task<byte[]> OptimizeImageForApiAsync(string filePath, CancellationToken ct)
        {
            const int maxWidth = 1600;
            const int maxHeight = 1600;
            const int maxSizeBytes = 4 * 1024 * 1024; // 4MB límite seguro
            
            using var image = await Image.LoadAsync(filePath, ct);
            
            // Redimensionar si es necesario
            if (image.Width > maxWidth || image.Height > maxHeight)
            {
                var ratio = Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height);
                var newWidth = (int)(image.Width * ratio);
                var newHeight = (int)(image.Height * ratio);
                
                image.Mutate(x => x.Resize(newWidth, newHeight));
                _logger.LogInformation("[QueueService] Imagen redimensionada de {OrigW}x{OrigH} a {NewW}x{NewH}", 
                    image.Width, image.Height, newWidth, newHeight);
            }
            
            // Convertir a JPEG con calidad ajustada
            using var ms = new MemoryStream();
            var encoder = new JpegEncoder() { Quality = 85 };
            await image.SaveAsJpegAsync(ms, encoder, ct);
            var bytes = ms.ToArray();
            
            // Si todavía es muy grande, reducir calidad
            if (bytes.Length > maxSizeBytes)
            {
                ms.SetLength(0);
                encoder = new JpegEncoder() { Quality = 75 };
                await image.SaveAsJpegAsync(ms, encoder, ct);
                bytes = ms.ToArray();
                _logger.LogInformation("[QueueService] Imagen recomprimida a calidad 75, tamaño: {Size} bytes", bytes.Length);
            }
            
            _logger.LogInformation("[QueueService] Imagen optimizada: {Size} bytes", bytes.Length);
            return bytes;
        }

        // TODO: Revisar que funciona
        private async Task ProcessItemAsync(AnalysisWorkItem item, CancellationToken ct)
        {
            _logger.LogInformation("[QueueService] Procesando item: TicketId={TicketId}, Attempt={Attempt}", item.TicketId, item.Attempt);

            // Scope inicial para marcar como 'Processing'
            using (var initialScope = _sp.CreateScope())
            {
                var db = initialScope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
                var ticket = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == item.TicketId, ct);
                if (ticket == null)
                {
                    _logger.LogWarning("[QueueService] Ticket {Id} no encontrado", item.TicketId);
                    return;
                }
                if (ticket.Status == "Completed") return; // skip
                ticket.Status = "Processing";
                await db.SaveChangesAsync(ct);
            }

            ExpenseTicket? ticketForUpdate = null;
            var log = new AnalysisLog { TicketId = item.TicketId, StartedAt = DateTime.UtcNow, Operation = "Queue", Attempt = item.Attempt };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Usar un scope dedicado para la lógica principal para evitar problemas de concurrencia con DbContext
                using var processingScope = _sp.CreateScope();
                var db = processingScope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
                ticketForUpdate = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == item.TicketId, ct);
                if (ticketForUpdate == null) throw new Exception("Ticket no encontrado en el segundo scope");

                var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
                var fileName = ticketForUpdate.FileName ?? string.Empty;
                var filePath = string.IsNullOrWhiteSpace(ticketForUpdate.FileUrl) ? null : Path.Combine(uploadsDir, fileName);
                if (filePath == null || !File.Exists(filePath))
                {
                    _logger.LogWarning("[QueueService] Archivo ticket {Id} no encontrado", ticketForUpdate.Id);
                    ticketForUpdate.Status = "Error"; 
                    ticketForUpdate.LastErrorMessage = "Archivo no encontrado";
                    log.Success = false; log.ErrorMessage = "File not found";
                    return;
                }

                var apiKey = _config["GROQ:ApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    _logger.LogError("[QueueService] ApiKey no configurada");
                    ticketForUpdate.Status = "Error"; 
                    ticketForUpdate.LastErrorMessage = "API Key no configurada";
                    log.Success = false; log.ErrorMessage = "API Key not configured";
                    return;
                }

                var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
                var model = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
                log.Model = model; log.Endpoint = endpoint; log.FileHashSnapshot = ticketForUpdate.FileHash;

                byte[] bytes = await OptimizeImageForApiAsync(filePath, ct);
                string mime = "image/jpeg"; // Siempre JPEG después de optimizar
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

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(endpoint, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    log.Success = false; log.ErrorMessage = $"HTTP {(int)resp.StatusCode}";
                    ticketForUpdate.Status = "Error";
                    ticketForUpdate.LastErrorMessage = $"Error HTTP {(int)resp.StatusCode}: {body}";
                }
                else
                {
                    string? contentText = null;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        contentText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        if (doc.RootElement.TryGetProperty("usage", out var usageEl))
                        {
                            log.JsonUsageRaw = usageEl.GetRawText();
                            if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind==System.Text.Json.JsonValueKind.Number) log.PromptTokens = pt.GetInt32();
                            if (usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind==System.Text.Json.JsonValueKind.Number) log.CompletionTokens = ctEl.GetInt32();
                            if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind==System.Text.Json.JsonValueKind.Number) log.TotalTokens = tt.GetInt32();
                        }
                    }
                    catch { contentText = body; }

                    string? markdownPart = null; string? jsonPart = contentText;
                    if (!string.IsNullOrWhiteSpace(contentText))
                    {
                        var splitIndex = contentText.IndexOf("}\n\n"); if (splitIndex < 0) splitIndex = contentText.IndexOf("}\r\n\r\n");
                        if (splitIndex > 0) { jsonPart = contentText.Substring(0, splitIndex + 1).Trim(); markdownPart = contentText.Substring(splitIndex + 3).Trim(); }
                    }

                    var jsonExtract = ExtractJson(jsonPart ?? string.Empty);
                    if (jsonExtract != null)
                    {
                        try
                        {
                            using var parsed = System.Text.Json.JsonDocument.Parse(jsonExtract);
                            if (parsed.RootElement.TryGetProperty("Amount", out var aEl))
                            {
                                if (aEl.ValueKind == System.Text.Json.JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) ticketForUpdate.Amount = dec;
                                else if (decimal.TryParse(aEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec2)) ticketForUpdate.Amount = dec2;
                            }
                            if (parsed.RootElement.TryGetProperty("Company", out var cEl)) ticketForUpdate.CompanyName = cEl.GetString();
                            if (parsed.RootElement.TryGetProperty("Category", out var catEl)) ticketForUpdate.Category = catEl.GetString();
                            ticketForUpdate.AnalysisJson = jsonExtract;
                            
                            if (!string.IsNullOrWhiteSpace(markdownPart))
                            {
                                var analysisFileName = ticketForUpdate.PublicId + "-analysis.md";
                                var analysisPath = Path.Combine(uploadsDir, analysisFileName);
                                await File.WriteAllTextAsync(analysisPath, markdownPart, System.Text.Encoding.UTF8, ct);
                                ticketForUpdate.AnalysisMarkdown = markdownPart;
                                ticketForUpdate.AnalysisFileName = analysisFileName;
                                ticketForUpdate.AnalysisFileUrl = $"/Uploads/{analysisFileName}";
                            }
                            ticketForUpdate.Status = "Completed";
                            log.Amount = ticketForUpdate.Amount; log.Company = ticketForUpdate.CompanyName; log.Category = ticketForUpdate.Category;
                            log.Success = true;
                        }
                        catch (Exception ex)
                        {
                            log.Success = false; log.ErrorMessage = "JSON parse fail"; ticketForUpdate.Status = "Error"; 
                            ticketForUpdate.LastErrorMessage = $"Error al parsear JSON: {ex.Message}";
                            _logger.LogWarning(ex, "[QueueService] Parse fail ticket {Id}", ticketForUpdate.Id);
                        }
                    }
                    else
                    {
                        log.Success = false; log.ErrorMessage = "No JSON"; ticketForUpdate.Status = "Error";
                        ticketForUpdate.LastErrorMessage = "No se encontró JSON en la respuesta del modelo";
                    }
                }
            }
            catch (Exception ex)
            {
                log.Success = false; log.ErrorMessage = ex.Message; 
                if(ticketForUpdate != null)
                {
                    ticketForUpdate.Status = "Error";
                    ticketForUpdate.LastErrorMessage = $"Excepción: {ex.Message}";
                }
                _logger.LogError(ex, "[QueueService] Excepcion ticket {Id}", item.TicketId);
            }
            finally
            {
                sw.Stop();
                log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds;
                try
                {
                    using var finalScope = _sp.CreateScope();
                    var db = finalScope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
                    db.AnalysisLogs.Add(log);
                    
                    if (ticketForUpdate != null)
                    {
                        // Cargar el ticket del nuevo contexto y actualizar sus valores
                        var ticketToSave = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == item.TicketId, ct);
                        if (ticketToSave != null)
                        {
                            ticketToSave.Status = ticketForUpdate.Status;
                            ticketToSave.Amount = ticketForUpdate.Amount;
                            ticketToSave.CompanyName = ticketForUpdate.CompanyName;
                            ticketToSave.Category = ticketForUpdate.Category;
                            ticketToSave.AnalysisJson = ticketForUpdate.AnalysisJson;
                            ticketToSave.AnalysisMarkdown = ticketForUpdate.AnalysisMarkdown;
                            ticketToSave.AnalysisFileName = ticketForUpdate.AnalysisFileName;
                            ticketToSave.AnalysisFileUrl = ticketForUpdate.AnalysisFileUrl;
                            ticketToSave.LastErrorMessage = ticketForUpdate.LastErrorMessage;
                        }
                    }
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[QueueService] No se pudo guardar el estado final y el log para el ticket {Id}", item.TicketId);
                }
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
    }
}
