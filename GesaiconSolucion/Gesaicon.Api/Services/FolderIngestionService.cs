using System.Security.Cryptography;
using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Gesaicon.Api.Diagnostics; // agregado

namespace Gesaicon.Api.Services;

public class FileIngestionOptions
{
    public bool Enabled { get; set; } = false;
    public string SourceFolder { get; set; } = "Incoming";
    public string BackupFolder { get; } = "IncomingBackup";
    public string ProcessedFolder { get; } = "IncomingProcessed";
    public string ErrorFolder { get; } = "IncomingError";
    public int ScanIntervalSeconds { get; set; } = 15;
    public int StableAgeSeconds { get; set; } = 5;
    public int MaxPerScan { get; set; } = 25;
    public bool EnqueueForAnalysis { get; set; } = true;
    public string[] AllowedExtensions { get; set; } = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
    public string DefaultCompanySlug { get; set; } = "default"; // temporal
}

public class FolderIngestionService : BackgroundService
{
    private readonly ILogger<FolderIngestionService> _logger;
    private readonly IServiceProvider _sp;
    private readonly IHostEnvironment _env;
    private readonly FileIngestionOptions _opt;
    private readonly IReceiptAnalysisQueue _queue;
    private readonly BackgroundStatusStore _status;

    public FolderIngestionService(
        ILogger<FolderIngestionService> logger,
        IServiceProvider sp,
        IHostEnvironment env,
        IOptions<FileIngestionOptions> opt,
        IReceiptAnalysisQueue queue,
        BackgroundStatusStore status)
    {
        _logger = logger;
        _sp = sp;
        _env = env;
        _opt = opt.Value;
        _queue = queue;
        _status = status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation("[Ingestion] Deshabilitado por configuración");
            _status.Update("FolderIngestion", "Deshabilitado", running: false);
            return;
        }

        _logger.LogInformation("[Ingestion] Iniciando servicio. Carpeta origen={Source}", _opt.SourceFolder);
        _status.Update("FolderIngestion", "Iniciado");
        EnsureFolders();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ingestion] Error general del ciclo");
                _status.Update("FolderIngestion", "Error ciclo: " + ex.Message, errorDelta: 1);
            }

            var delay = TimeSpan.FromSeconds(Math.Max(3, _opt.ScanIntervalSeconds));
            await Task.Delay(delay, stoppingToken);
        }
        _logger.LogInformation("[Ingestion] Servicio detenido");
        _status.Update("FolderIngestion", "Detenido", running: false);
    }

    private void EnsureFolders()
    {
        foreach (var rel in new[] { _opt.SourceFolder, _opt.BackupFolder, _opt.ProcessedFolder, _opt.ErrorFolder })
        {
            var path = Path.Combine(_env.ContentRootPath, rel);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }
    }

    private async Task ScanAndProcessAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var srcPath = Path.Combine(_env.ContentRootPath, _opt.SourceFolder);
        if (!Directory.Exists(srcPath))
        {
            _logger.LogWarning("[Ingestion] Carpeta origen no existe: {Path}", srcPath);
            return;
        }

        var files = Directory.GetFiles(srcPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => !_opt.AllowedExtensions.Any() || _opt.AllowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => File.GetLastWriteTimeUtc(f))
            .ToList();

        if (!files.Any()) return;

        var stableAge = TimeSpan.FromSeconds(Math.Max(1, _opt.StableAgeSeconds));
        var candidates = files
            .Where(f => (now - File.GetLastWriteTimeUtc(f)) >= stableAge)
            .Take(Math.Max(1, _opt.MaxPerScan))
            .ToList();

        if (!candidates.Any()) return;

        _logger.LogInformation("[Ingestion] {Count} archivo(s) candidato(s) para procesar", candidates.Count);
        _status.Update("FolderIngestion", $"Procesando {candidates.Count} archivo(s)..." );

        foreach (var filePath in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ProcessFileAsync(filePath, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ingestion] Error procesando {File}", Path.GetFileName(filePath));
                _status.Update("FolderIngestion", $"Error archivo {Path.GetFileName(filePath)}", errorDelta:1);
                MoveToError(filePath, "error");
            }
        }
    }

    private async Task ProcessFileAsync(string originalPath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(originalPath);
        var processedDir = Path.Combine(_env.ContentRootPath, _opt.ProcessedFolder);
        var backupDir = Path.Combine(_env.ContentRootPath, _opt.BackupFolder);

        if (!CanReadExclusive(originalPath))
        {
            _logger.LogDebug("[Ingestion] Archivo en uso, se pospone: {File}", fileName);
            return;
        }

        byte[] data;
        using (var ms = new MemoryStream())
        {
            await using var fs = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await fs.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }

        string hash;
        using (var sha = SHA256.Create())
            hash = Convert.ToHexString(sha.ComputeHash(data));

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();

        var dup = await db.ExpenseTickets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.FileHash == hash, ct);

        if (dup != null)
        {
            _logger.LogInformation("[Ingestion] Archivo {File} duplicado de TicketId={Id}", fileName, dup.Id);
            MoveToProcessed(originalPath, processedDir, "DUP");
            return;
        }

        var backupTarget = Path.Combine(backupDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{fileName}");
        try
        {
            File.Copy(originalPath, backupTarget, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ingestion] No se pudo crear backup para {File}", fileName);
        }

        var publicId = Guid.NewGuid();
        
        // NO usar fecha de subida. Siempre guardar en carpeta placeholder 0000/00 hasta conocer fecha real.
        const int placeholderYear = 0; // se serializa como 0000
        const int placeholderMonth = 0; // 00
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        StoredFileInfo stored;
        
        using (var ms = new MemoryStream(data))
        {
            stored = await storage.SaveTicketFileAsync(
                ms,
                _opt.DefaultCompanySlug,
                placeholderYear,
                placeholderMonth,
                publicId.ToString("N"),
                fileName,
                ct);
        }

        var ticket = new ExpenseTicket
        {
            PublicId = publicId,
            CompanySlug = _opt.DefaultCompanySlug,
            ExpenseYear = null,          // NO asignar fecha de subida
            ExpenseMonth = null,
            FileName = stored.OriginalFileName,
            RelativePath = stored.RelativePath,
            FileUrl = $"/Uploads/{stored.RelativePath}",
            FileSizeBytes = stored.SizeBytes,
            FileHash = stored.Hash,
            UploadedAt = DateTime.UtcNow,
            Status = _opt.EnqueueForAnalysis ? "PendingAnalysis" : "Uploaded",
            RetryCount = 0
        };
        db.ExpenseTickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        if (_opt.EnqueueForAnalysis)
        {
            await _queue.EnqueueAsync(ticket.Id, 0);
            _logger.LogInformation("[Ingestion] Ticket {Id} encolado (ruta temporal empresas/default/0000/00/ hasta conocer fecha real)", ticket.Id);
        }
        else
        {
            _logger.LogInformation("[Ingestion] Ticket {Id} creado sin encolar (config)", ticket.Id);
        }

        _status.Update("FolderIngestion", $"Ticket {ticket.Id} creado", processedDelta:1);
        MoveToProcessed(originalPath, processedDir, "OK");
    }

    private bool CanReadExclusive(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void MoveToProcessed(string originalPath, string processedDir, string tag)
    {
        try
        {
            if (!Directory.Exists(processedDir)) Directory.CreateDirectory(processedDir);
            var name = Path.GetFileNameWithoutExtension(originalPath);
            var ext = Path.GetExtension(originalPath);
            var targetName = $"{name}_{tag}_{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}";
            var target = Path.Combine(processedDir, targetName);
            File.Move(originalPath, target, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ingestion] No se pudo mover a procesados: {File}", originalPath);
        }
    }

    private void MoveToError(string originalPath, string? reason)
    {
        try
        {
            var errDir = Path.Combine(_env.ContentRootPath, _opt.ErrorFolder);
            if (!Directory.Exists(errDir)) Directory.CreateDirectory(errDir);
            var name = Path.GetFileNameWithoutExtension(originalPath);
            var ext = Path.GetExtension(originalPath);
            var targetName = $"{name}_ERR_{reason}_{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}";
            var target = Path.Combine(errDir, targetName);
            File.Move(originalPath, target, overwrite: true);
        }
        catch { }
    }
}
