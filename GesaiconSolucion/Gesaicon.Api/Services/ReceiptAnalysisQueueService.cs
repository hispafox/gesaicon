using System.Threading.Channels;
using Gesaicon.Api.Data;
using Microsoft.EntityFrameworkCore;
using Gesaicon.Api.Models;

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


        // TODO: Revisar que funciona
        private async Task ProcessItemAsync(AnalysisWorkItem item, CancellationToken ct)
        {
            _logger.LogInformation("[QueueService] Procesando item: TicketId={TicketId}, Attempt={Attempt}", item.TicketId, item.Attempt);
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var ticket = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == item.TicketId, ct);
            if (ticket == null)
            {
                _logger.LogWarning("[QueueService] Ticket {Id} no encontrado", item.TicketId);
                return;
            }
            if (ticket.Status == "Completed") return; // skip
            ticket.Status = "Processing";
            await db.SaveChangesAsync(ct);

            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            var fileName = ticket.FileName ?? string.Empty;
            var filePath = string.IsNullOrWhiteSpace(ticket.FileUrl) ? null : Path.Combine(uploadsDir, fileName);
            if (filePath == null || !File.Exists(filePath))
            {
                _logger.LogWarning("[QueueService] Archivo ticket {Id} no encontrado", ticket.Id);
                ticket.Status = "Error"; await db.SaveChangesAsync(ct); return;
            }

            var apiKey = _config["GROQ:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("[QueueService] ApiKey no configurada");
                ticket.Status = "Error"; await db.SaveChangesAsync(ct); return;
            }

            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
            var model = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";

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

            var log = new AnalysisLog
            {
                TicketId = ticket.Id,
                StartedAt = DateTime.UtcNow,
                Operation = "Queue",
                Attempt = item.Attempt,
                Model = model,
                Endpoint = endpoint,
                FileHashSnapshot = ticket.FileHash
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(endpoint, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    log.Success = false; log.ErrorMessage = $"HTTP {(int)resp.StatusCode}";
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
                                if (aEl.ValueKind == System.Text.Json.JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) ticket.Amount = dec;
                                else if (decimal.TryParse(aEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec2)) ticket.Amount = dec2;
                            }
                            if (parsed.RootElement.TryGetProperty("Company", out var cEl)) ticket.CompanyName = cEl.GetString();
                            if (parsed.RootElement.TryGetProperty("Category", out var catEl)) ticket.Category = catEl.GetString();
                            ticket.AnalysisJson = jsonExtract;
                            // markdown file generation
                            string? analysisFileName = null; string? analysisFileUrl = null;
                            if (!string.IsNullOrWhiteSpace(markdownPart))
                            {
                                var uploads = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
                                if (!Directory.Exists(uploads)) Directory.CreateDirectory(uploads);
                                analysisFileName = ticket.PublicId + "-analysis.md";
                                var analysisPath = Path.Combine(uploads, analysisFileName);
                                await File.WriteAllTextAsync(analysisPath, markdownPart, System.Text.Encoding.UTF8, ct);
                                analysisFileUrl = $"/Uploads/{analysisFileName}";
                                ticket.AnalysisMarkdown = markdownPart;
                                ticket.AnalysisFileName = analysisFileName;
                                ticket.AnalysisFileUrl = analysisFileUrl;
                            }
                            ticket.Status = "Completed";
                            log.Amount = ticket.Amount; log.Company = ticket.CompanyName; log.Category = ticket.Category;
                            log.Success = true;
                        }
                        catch (Exception ex)
                        {
                            log.Success = false; log.ErrorMessage = "JSON parse fail"; ticket.Status = "Error"; _logger.LogWarning(ex, "[QueueService] Parse fail ticket {Id}", ticket.Id);
                        }
                    }
                    else
                    {
                        log.Success = false; log.ErrorMessage = "No JSON"; ticket.Status = "Error";
                    }
                }
            }
            catch (Exception ex)
            {
                log.Success = false; log.ErrorMessage = ex.Message; ticket.Status = "Error"; _logger.LogError(ex, "[QueueService] Excepcion ticket {Id}", ticket.Id);
            }
            finally
            {
                sw.Stop();
                log.FinishedAt = DateTime.UtcNow; log.DurationMs = sw.ElapsedMilliseconds;
                try
                {
                    db.AnalysisLogs.Add(log);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[QueueService] No se pudo guardar AnalysisLog");
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
