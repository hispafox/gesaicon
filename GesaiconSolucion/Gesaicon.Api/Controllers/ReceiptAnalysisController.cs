using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using Gesaicon.Api.Models;
using SixLabors.ImageSharp; // para redimensionar
using SixLabors.ImageSharp.Processing;

namespace Gesaicon.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReceiptAnalysisController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<ReceiptAnalysisController> _logger;

        public ReceiptAnalysisController(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ReceiptAnalysisController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        [HttpPost("analyze")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(ReceiptAnalysisResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Analyze([FromForm] UploadExpenseTicketRequest request, [FromQuery] int? ticketId = null)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest("Archivo requerido");

            _logger.LogInformation("[Analyze] Inicio análisis. ticketId={TicketId} nombreOriginal={Name} size={Size} bytes", ticketId, request.File.FileName, request.File.Length);

            var apiKey = _config["GROQ:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                return StatusCode(500, "GROQ ApiKey no configurada");

            // Guardar temporalmente el archivo en Uploads para obtener una URL accesible
            var uploads = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            if (!Directory.Exists(uploads)) Directory.CreateDirectory(uploads);
            var fileName = Guid.NewGuid() + Path.GetExtension(request.File.FileName);
            var filePath = Path.Combine(uploads, fileName);
            using (var fs = new FileStream(filePath, FileMode.Create))
            {
                await request.File.CopyToAsync(fs);
            }
            _logger.LogInformation("[Analyze] Archivo guardado en {Path}", filePath);

            // Construir URL pública (asumiendo HTTPS si se accede por swagger). Si detrás de proxy ajustar.
            var scheme = HttpContext.Request.Scheme; // https o http
            var host = HttpContext.Request.Host.Value; // host:puerto
            var publicUrl = $"{scheme}://{host}/Uploads/{fileName}";
            bool useDataUrl = host.Contains("localhost", StringComparison.OrdinalIgnoreCase);

            string? imageReference;
            if (useDataUrl)
            {
                _logger.LogDebug("[Analyze] Usando data URL (localhost) para imagen");
                // Redimensionar si es grande y convertir a base64
                await using var imgStream = System.IO.File.OpenRead(filePath);
                using var image = await Image.LoadAsync(imgStream);
                const int maxDim = 1600; // limitar tamaño
                if (image.Width > maxDim || image.Height > maxDim)
                {
                    _logger.LogDebug("[Analyze] Redimensionando imagen de {W}x{H} (max {Max})", image.Width, image.Height, maxDim);
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(maxDim, maxDim)
                    }));
                }
                await using var ms = new MemoryStream();
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext is ".png")
                    await image.SaveAsPngAsync(ms);
                else
                    await image.SaveAsJpegAsync(ms);
                var b64 = Convert.ToBase64String(ms.ToArray());
                var mime = ext == ".png" ? "image/png" : "image/jpeg";
                imageReference = $"data:{mime};base64,{b64}";
                _logger.LogDebug("[Analyze] Imagen convertida a base64 tamaño={Len} chars", b64.Length);
            }
            else
            {
                _logger.LogDebug("[Analyze] Usando URL pública {Url}", publicUrl);
                imageReference = publicUrl;
            }

            var visionModel = _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct"; // modelo vision soportado
            _logger.LogInformation("[Analyze] Modelo seleccionado {Model}", visionModel);

            // Prompt único corregido (antes había duplicado)
            var prompt = "Devuelve primero SOLO un JSON estricto con claves Amount (decimal '.'), Company (string), Category (Food, Transport, Office, Grocery, Pharmacy, Other) y luego tras una linea vacía un análisis económico completo del ticket en Markdown (español) con: lista de productos (cantidades, precios, subtotales), resumen de bases imponibles e IVA si aparecen, total, forma de pago, nº artículos, fecha/hora, dirección, número de ticket/factura, datos fiscales (CIF/NIF) y cualquier otro dato de comercio. No incluyas backticks en el JSON. Si falta algún dato usa null. Formato JSON ejemplo: {\"Amount\": 0.00, \"Company\": null, \"Category\": null}.";

            var payload = new
            {
                model = visionModel,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]{
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = imageReference } }
                        }
                    }
                },
                temperature = 0,
                max_completion_tokens = 300
            };

            var json = JsonSerializer.Serialize(payload);
            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
            _logger.LogDebug("[Analyze] Payload length={Len} chars", json.Length);

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(endpoint, content);
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("[Analyze] Respuesta Groq status={Status} length={Len} chars", response.StatusCode, body.Length);

            // Fallback si modelo no existe
            if (!response.IsSuccessStatusCode && body.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) && visionModel != "meta-llama/llama-4-scout-17b-16e-instruct")
            {
                _logger.LogWarning("[Analyze] Modelo {OldModel} no encontrado. Reintentando con fallback", visionModel);
                visionModel = "meta-llama/llama-4-scout-17b-16e-instruct";
                var retryPayload = new
                {
                    model = visionModel,
                    messages = ((object[])payload.messages),
                    temperature = 0,
                    max_completion_tokens = 300
                };
                var retryJson = JsonSerializer.Serialize(retryPayload);
                using var retryContent = new StringContent(retryJson, Encoding.UTF8, "application/json");
                response = await client.PostAsync(endpoint, retryContent);
                body = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("[Analyze] Respuesta reintento status={Status} length={Len} chars", response.StatusCode, body.Length);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Groq vision fallo Status={Status} Body={Body}", response.StatusCode, body);
                return StatusCode((int)response.StatusCode, new { error = "Groq error", body });
            }

            // Obtener texto devuelto
            string? rawText = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                rawText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo leer content de la respuesta Groq");
                return Ok(new ReceiptAnalysisResult { Raw = body, Model = visionModel });
            }
            _logger.LogDebug("[Analyze] Longitud rawText={Len}", rawText?.Length);

            // Separar JSON y Markdown
            string? markdownPart = null;
            string? jsonPart = rawText;
            if (!string.IsNullOrWhiteSpace(rawText))
            {
                var splitIndex = rawText.IndexOf("}\n\n");
                if (splitIndex < 0) splitIndex = rawText.IndexOf("}\r\n\r\n");
                if (splitIndex > 0)
                {
                    jsonPart = rawText.Substring(0, splitIndex + 1).Trim();
                    markdownPart = rawText.Substring(splitIndex + 3).Trim();
                    _logger.LogDebug("[Analyze] Separado JSON ({JsonLen} chars) y Markdown ({MdLen} chars)", (splitIndex + 1), rawText.Length - (splitIndex + 3));
                }
                else
                {
                    _logger.LogWarning("[Analyze] No se pudo separar JSON y Markdown (patrón no encontrado)");
                }
            }
            else
            {
                _logger.LogWarning("[Analyze] rawText vacío o nulo");
            }

            var jsonFragment = ExtractJson(jsonPart ?? string.Empty);
            decimal? amount = null; string? company = null; string? category = null;
            if (jsonFragment != null)
            {
                _logger.LogDebug("[Analyze] JSON fragment length={Len}", jsonFragment.Length);
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
                    _logger.LogInformation("[Analyze] Valores parseados Amount={Amount} Company={Company} Category={Category}", amount, company, category);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Analyze] Fallo parseando JSON devuelto");
                }
            }
            else
            {
                _logger.LogWarning("[Analyze] No se pudo extraer fragmento JSON");
            }

            // Construir JSON combinado para almacenar
            string? combinedJson = null;
            try
            {
                var combined = new
                {
                    Summary = new { Amount = amount, Company = company, Category = category },
                    AnalysisMarkdown = markdownPart,
                    Model = visionModel,
                    FileName = fileName,
                    Source = "ReceiptAnalysisController.Analyze"
                };
                combinedJson = JsonSerializer.Serialize(combined, new JsonSerializerOptions { WriteIndented = false });
                _logger.LogDebug("[Analyze] combinedJson length={Len} chars", combinedJson.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Analyze] No se pudo serializar el JSON combinado del análisis");
            }

            // Actualizar ticket existente si se pasa ticketId
            if (ticketId.HasValue)
            {
                _logger.LogInformation("[Analyze] Actualizando ticket {TicketId} en BD", ticketId);
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Gesaicon.Api.Data.GesaiconDbContext>();
                var ticket = await db.ExpenseTickets.FindAsync(ticketId.Value);
                if (ticket != null)
                {
                    if (amount.HasValue) ticket.Amount = amount;
                    if (!string.IsNullOrWhiteSpace(company)) ticket.CompanyName = company;
                    if (!string.IsNullOrWhiteSpace(category)) ticket.Category = category;
                    ticket.AnalysisJson = combinedJson ?? jsonFragment;
                    ticket.AnalysisMarkdown = markdownPart;
                    ticket.Status = "Completed";
                    await db.SaveChangesAsync();
                    _logger.LogInformation("[Analyze] Ticket {TicketId} actualizado (Status=Completed)", ticketId);
                }
                else
                {
                    _logger.LogWarning("[Analyze] ticketId {TicketId} no encontrado para actualizar", ticketId);
                }
            }

            return Ok(new
            {
                Amount = amount,
                Company = company,
                Category = category,
                AnalysisJson = combinedJson ?? jsonFragment,
                AnalysisMarkdown = markdownPart,
                Raw = rawText,
                Model = visionModel
            });
        }

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

        [HttpPost("analyze/advanced")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(ReceiptAnalysisResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> AnalyzeAdvanced([FromForm] ReceiptAnalysisRequest request, CancellationToken ct)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest("Archivo requerido");
            _logger.LogInformation("[AnalyzeAdvanced] Inicio análisis avanzado. nombreOriginal={Name} size={Size} stream={Stream} jsonMode={JsonMode}", request.File.FileName, request.File.Length, request.Stream, request.JsonMode);

            var apiKey = _config["GROQ:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                return StatusCode(500, "GROQ ApiKey no configurada");

            // Guardar archivo
            var uploads = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            if (!Directory.Exists(uploads)) Directory.CreateDirectory(uploads);
            var fileName = Guid.NewGuid() + Path.GetExtension(request.File.FileName);
            var filePath = Path.Combine(uploads, fileName);
            using (var fs = new FileStream(filePath, FileMode.Create))
            {
                await request.File.CopyToAsync(fs, ct);
            }
            _logger.LogInformation("[AnalyzeAdvanced] Archivo guardado en {Path}", filePath);

            // Construir URL pública (asumiendo HTTPS si se accede por swagger). Si detrás de proxy ajustar.
            var scheme = HttpContext.Request.Scheme;
            var host = HttpContext.Request.Host.Value;
            var publicUrl = $"{scheme}://{host}/Uploads/{fileName}";
            bool useDataUrl = host.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            string? imageReference;
            if (useDataUrl)
            {
                _logger.LogDebug("[AnalyzeAdvanced] Usando data URL (localhost) para imagen");
                // Redimensionar si es grande y convertir a base64
                await using var imgStream2 = System.IO.File.OpenRead(filePath);
                using var image2 = await Image.LoadAsync(imgStream2, ct);
                const int maxDim2 = 1600;
                if (image2.Width > maxDim2 || image2.Height > maxDim2)
                {
                    _logger.LogDebug("[AnalyzeAdvanced] Redimensionando imagen de {W}x{H} (max {Max})", image2.Width, image2.Height, maxDim2);
                    image2.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(maxDim2, maxDim2)
                    }));
                }
                await using var ms2 = new MemoryStream();
                var ext2 = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext2 is ".png")
                    await image2.SaveAsPngAsync(ms2, ct);
                else
                    await image2.SaveAsJpegAsync(ms2, ct);
                var b642 = Convert.ToBase64String(ms2.ToArray());
                var mime2 = ext2 == ".png" ? "image/png" : "image/jpeg";
                imageReference = $"data:{mime2};base64,{b642}";
                _logger.LogDebug("[AnalyzeAdvanced] Imagen convertida a base64 tamaño={Len} chars", b642.Length);
            }
            else
            {
                _logger.LogDebug("[AnalyzeAdvanced] Usando URL pública {Url}", publicUrl);
                imageReference = publicUrl;
            }

            var model = request.Model ?? _config["GROQ:VisionModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
            var basePrompt = "Extrae SOLO un JSON con Amount (decimal '.'), Company, Category (Food, Transport, Office, Grocery, Pharmacy, Other). Usa null si falta. Devuelve solo JSON.";
            if (!string.IsNullOrWhiteSpace(request.ExtraPrompt)) basePrompt += " " + request.ExtraPrompt;

            var payload = new
            {
                model,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]{
                            new { type = "text", text = basePrompt },
                            new { type = "image_url", image_url = new { url = imageReference } }
                        }
                    }
                },
                temperature = request.Temperature ?? 0m,
                max_completion_tokens = request.MaxTokens ?? 300,
                top_p = request.TopP ?? 1m,
                stream = request.Stream,
                response_format = request.JsonMode ? new { type = "json_object" } : null
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions{ DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";
            _logger.LogDebug("[AnalyzeAdvanced] Payload length={Len} chars", json.Length);

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            if (request.Stream)
            {
                // Streaming SSE
                var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                httpReq.Headers.Accept.ParseAdd("text/event-stream");
                var httpResp = await client.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
                _logger.LogInformation("[AnalyzeAdvanced] Respuesta streaming status={Status}", httpResp.StatusCode);
                if (!httpResp.IsSuccessStatusCode)
                {
                    var errBody = await httpResp.Content.ReadAsStringAsync(ct);
                    return StatusCode((int)httpResp.StatusCode, new { error = errBody });
                }
                var stream = await httpResp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                var sb = new StringBuilder();
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    if (!line.StartsWith("data: ")) continue;
                    var payloadLine = line[6..].Trim();
                    if (payloadLine == "[DONE]") break;
                    try
                    {
                        using var doc = JsonDocument.Parse(payloadLine);
                        var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var cEl)) sb.Append(cEl.GetString());
                    }
                    catch { /* ignorar trozos parciales */ }
                }
                var full = sb.ToString();
                _logger.LogDebug("[AnalyzeAdvanced] Texto acumulado length={Len}", full.Length);
                var jsonFragment = ExtractJson(full);
                decimal? amount = null; string? company = null; string? category = null;
                if (jsonFragment != null)
                {
                    try
                    {
                        using var parsed = JsonDocument.Parse(jsonFragment);
                        if (parsed.RootElement.TryGetProperty("Amount", out var aEl) && aEl.ValueKind == JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) amount = dec;
                        if (parsed.RootElement.TryGetProperty("Company", out var cEl)) company = cEl.GetString();
                        if (parsed.RootElement.TryGetProperty("Category", out var catEl)) category = catEl.GetString();
                    }
                    catch {}
                }
                return Ok(new ReceiptAnalysisResult { Amount = amount, Company = company, Category = category, Raw = full, Model = model });
            }
            else
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(endpoint, content, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("[AnalyzeAdvanced] Respuesta status={Status} length={Len} chars", response.StatusCode, body.Length);
                if (!response.IsSuccessStatusCode && body.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) && model != "meta-llama/llama-4-scout-17b-16e-instruct")
                {
                    _logger.LogWarning("[AnalyzeAdvanced] Modelo {OldModel} no encontrado. Reintentando con fallback", model);
                    model = "meta-llama/llama-4-scout-17b-16e-instruct";
                    var retryPayload = new
                    {
                        model,
                        messages = ((object[])payload.messages),
                        temperature = request.Temperature ?? 0m,
                        max_completion_tokens = request.MaxTokens ?? 300,
                        top_p = request.TopP ?? 1m,
                        stream = false
                    };
                    var retryJson = JsonSerializer.Serialize(retryPayload);
                    using var retryContent = new StringContent(retryJson, Encoding.UTF8, "application/json");
                    response = await client.PostAsync(endpoint, retryContent, ct);
                    body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogInformation("[AnalyzeAdvanced] Respuesta reintento status={Status} length={Len} chars", response.StatusCode, body.Length);
                }
                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, new { error = body });
                string? rawText = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    rawText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                }
                catch { rawText = body; }
                var jsonFragment = ExtractJson(rawText ?? string.Empty);
                decimal? amount = null; string? company = null; string? category = null;
                if (jsonFragment != null)
                {
                    try
                    {
                        using var parsed = JsonDocument.Parse(jsonFragment);
                        if (parsed.RootElement.TryGetProperty("Amount", out var aEl) && aEl.ValueKind == JsonValueKind.Number && aEl.TryGetDecimal(out var dec)) amount = dec;
                        if (parsed.RootElement.TryGetProperty("Company", out var cEl)) company = cEl.GetString();
                        if (parsed.RootElement.TryGetProperty("Category", out var catEl)) category = catEl.GetString();
                    }
                    catch {}
                }
                return Ok(new ReceiptAnalysisResult { Amount = amount, Company = company, Category = category, Raw = rawText, Model = model });
            }
        }
    }
}
