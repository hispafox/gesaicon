using System.Threading.Channels;
using Gesaicon.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gesaicon.Api.Services;

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
    private readonly IReceiptAnalysisProcessor _processor;

    private readonly int _maxAttempts;
    private readonly int _retryBackoffSeconds;

    public ReceiptAnalysisQueueService(
        IServiceProvider sp,
        ILogger<ReceiptAnalysisQueueService> logger,
        IConfiguration config,
        IReceiptAnalysisProcessor processor)
    {
        _sp = sp;
        _logger = logger;
        _config = config;
        _processor = processor;

        var capacity = _config.GetValue<int?>("Analysis:Queue:Capacity") ?? 500;
        _maxAttempts = _config.GetValue<int?>("Analysis:Queue:MaxAttempts") ?? 3;
        _retryBackoffSeconds = _config.GetValue<int?>("Analysis:Queue:RetryBackoffSeconds") ?? 30;

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<AnalysisWorkItem>(options);
    }

    public ValueTask EnqueueAsync(int ticketId, int attempt = 1)
    {
        return _channel.Writer.WriteAsync(new AnalysisWorkItem(ticketId, attempt));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Queue] Worker iniciado (MaxAttempts={Max}, Backoff={Backoff}s)", _maxAttempts, _retryBackoffSeconds);
        await SeedPendingAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            AnalysisWorkItem item;
            try
            {
                item = await _channel.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Queue] Error leyendo canal");
                continue;
            }

            _ = Task.Run(() => ProcessAsync(item, stoppingToken), stoppingToken);
        }

        _logger.LogInformation("[Queue] Worker detenido");
    }

    private async Task SeedPendingAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var tickets = await db.ExpenseTickets
                .Where(t => t.Status == "PendingAnalysis" || (t.Status == "Error" && t.RetryCount < _maxAttempts))
                .Select(t => new { t.Id, t.RetryCount, t.Status })
                .ToListAsync(ct);

            if (!tickets.Any())
            {
                _logger.LogInformation("[Queue] No hay tickets pendientes para seed inicial");
                return;
            }

            foreach (var t in tickets)
            {
                var nextAttempt = t.RetryCount == 0 ? 1 : t.RetryCount + 1;
                await EnqueueAsync(t.Id, nextAttempt);
            }

            _logger.LogInformation("[Queue] Seed inicial encoló {Count} tickets", tickets.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Queue] Error en seed inicial");
        }
    }

    private async Task ProcessAsync(AnalysisWorkItem item, CancellationToken ct)
    {
        _logger.LogInformation("[Queue] Procesando TicketId={Id} Attempt={Attempt}", item.TicketId, item.Attempt);

        bool success;
        try
        {
            success = await _processor.AnalyzeAsync(item.TicketId, item.Attempt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Queue] Excepción no controlada en AnalyzeAsync TicketId={Id}", item.TicketId);
            success = false;
        }

        if (!success)
        {
            await HandleRetryAsync(item, ct);
        }
    }

    private async Task HandleRetryAsync(AnalysisWorkItem item, CancellationToken ct)
    {
        if (item.Attempt >= _maxAttempts)
        {
            _logger.LogWarning("[Queue] Ticket {Id} alcanzó max intentos ({Attempt}/{Max})", item.TicketId, item.Attempt, _maxAttempts);
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
                var ticket = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == item.TicketId, ct);
                if (ticket != null)
                {
                    ticket.RetryCount = item.Attempt;
                    if (ticket.Status != "Completed" && ticket.Status != "Processing")
                        ticket.Status = "Error";
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Queue] No se pudo actualizar RetryCount final TicketId={Id}", item.TicketId);
            }
            return;
        }

        _logger.LogInformation("[Queue] Reintentando Ticket {Id} en {Backoff}s (Attempt {Next}/{Max})", item.TicketId, _retryBackoffSeconds, item.Attempt + 1, _maxAttempts);

        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var ticket = await db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == item.TicketId, ct);
            if (ticket != null)
            {
                ticket.RetryCount = item.Attempt;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Queue] No se pudo actualizar RetryCount intermedio TicketId={Id}", item.TicketId);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_retryBackoffSeconds), ct);
                await EnqueueAsync(item.TicketId, item.Attempt + 1);
            }
            catch { }
        }, ct);
    }
}
