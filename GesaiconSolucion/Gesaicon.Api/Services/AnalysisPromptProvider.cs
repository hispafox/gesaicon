namespace Gesaicon.Api.Services;

public interface IAnalysisPromptProvider
{
    string GetPrompt();
}

public class AnalysisPromptProvider : IAnalysisPromptProvider
{
    private readonly string _contentRoot;
    private readonly IConfiguration _config;
    private readonly ILogger<AnalysisPromptProvider> _logger;
    private string? _cached;
    private DateTime _lastRead;
    private readonly object _lock = new();
    private const string RelativePath = "Templates/analysis_prompt.md";
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public AnalysisPromptProvider(string contentRoot, IConfiguration config, ILogger<AnalysisPromptProvider> logger)
    {
        _contentRoot = contentRoot;
        _config = config;
        _logger = logger;
    }

    public string GetPrompt()
    {
        lock (_lock)
        {
            if (_cached != null && (DateTime.UtcNow - _lastRead) < _cacheDuration)
                return _cached;

            var filePath = Path.Combine(_contentRoot, RelativePath);
            if (File.Exists(filePath))
            {
                try
                {
                    _cached = File.ReadAllText(filePath);
                    _lastRead = DateTime.UtcNow;
                    return _cached!;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PromptProvider] Error leyendo archivo externo, usando config");
                }
            }

            _cached = _config["Analysis:Prompt"] ?? "Devuelve primero SOLO un JSON estricto con claves Amount (decimal '.'), Company (string), Category (Food, Transport, Office, Grocery, Pharmacy, Other) y luego tras una linea vacía un análisis económico completo del ticket en Markdown (español). No incluyas backticks. Usa null cuando falte dato.";
            _lastRead = DateTime.UtcNow;
            return _cached!;
        }
    }
}
