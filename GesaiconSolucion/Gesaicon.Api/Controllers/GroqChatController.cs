using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Gesaicon.Api.Models;

namespace Gesaicon.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GroqChatController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<GroqChatController> _logger;
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
        private const string FallbackModel = "meta-llama/llama-4-scout-17b-16e-instruct";

        public GroqChatController(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<GroqChatController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        [HttpPost("chat")]
        [ProducesResponseType(typeof(ChatCompletionResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CreateChat([FromBody] ChatCompletionRequestDto request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return BadRequest("Prompt requerido");
            }

            var apiKey = _config["GROQ:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("[GroqChat] GROQ:ApiKey no configurada");
                return StatusCode(500, "API key no configurada");
            }

            var http = _httpClientFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var model = string.IsNullOrWhiteSpace(request.Model) ? FallbackModel : request.Model!;
            var endpoint = _config["GROQ:ChatEndpoint"] ?? "https://api.groq.com/openai/v1/chat/completions";

            var payload = new
            {
                model,
                messages = new object[] { new { role = "user", content = request.Prompt } },
                temperature = request.Temperature ?? 1m,
                max_completion_tokens = request.MaxTokens ?? 512,
                top_p = request.TopP ?? 1m
            };

            string json = JsonSerializer.Serialize(payload, JsonOpts);
            _logger.LogInformation("[GroqChat] Enviando solicitud Modelo={Model} temp={Temp} maxTokens={Max}", model, payload.temperature, payload.max_completion_tokens);
            _logger.LogDebug("[GroqChat] PayloadLength={Len} chars", json.Length);

            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await http.PostAsync(endpoint, content, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("[GroqChat] Status={Status} BodyLength={Len}", response.StatusCode, body.Length);

                // Fallback de modelo si procede
                if (!response.IsSuccessStatusCode && body.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) && model != FallbackModel)
                {
                    _logger.LogWarning("[GroqChat] Modelo {OldModel} no encontrado. Reintentando con fallback {Fallback}", model, FallbackModel);
                    var retry = new
                    {
                        model = FallbackModel,
                        messages = payload.messages,
                        temperature = payload.temperature,
                        max_completion_tokens = payload.max_completion_tokens,
                        top_p = payload.top_p
                    };
                    var retryJson = JsonSerializer.Serialize(retry, JsonOpts);
                    using var retryContent = new StringContent(retryJson, Encoding.UTF8, "application/json");
                    response = await http.PostAsync(endpoint, retryContent, ct);
                    body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogInformation("[GroqChat] Reintento Status={Status} BodyLength={Len}", response.StatusCode, body.Length);
                    model = FallbackModel; // actualizar para la respuesta final
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("[GroqChat] Error Status={Status} Body={Body}", response.StatusCode, body);
                    return StatusCode((int)response.StatusCode, new { error = "Groq error", detail = body });
                }

                GroqChatApiResponse? apiResp = null;
                try
                {
                    apiResp = JsonSerializer.Deserialize<GroqChatApiResponse>(body, JsonOpts);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[GroqChat] Error deserializando respuesta");
                    return Ok(new ChatCompletionResponseDto { Raw = body, Content = null, Model = model });
                }

                var contentText = apiResp?.choices?.FirstOrDefault()?.message?.content;
                _logger.LogDebug("[GroqChat] ContentLength={Len}", contentText?.Length ?? 0);

                return Ok(new ChatCompletionResponseDto
                {
                    Content = contentText,
                    Model = apiResp?.model ?? model,
                    Raw = body
                });
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogWarning("[GroqChat] Solicitud cancelada por el cliente");
                return StatusCode(499, new { error = "Client Cancelled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GroqChat] Excepción ejecutando chat");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
