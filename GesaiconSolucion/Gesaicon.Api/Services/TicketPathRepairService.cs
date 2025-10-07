using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gesaicon.Api.Services
{
    /// <summary>
    /// Servicio que recorre todos los tickets, verifica sus rutas físicas, 
    /// busca archivos perdidos recursivamente, extrae fechas del análisis,
    /// y mueve los archivos a la estructura correcta.
    /// </summary>
    public class TicketPathRepairService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<TicketPathRepairService> _logger;
        private readonly IHostEnvironment _env;
        private readonly string _tempBackupDir;

        public TicketPathRepairService(
            IServiceProvider sp,
            ILogger<TicketPathRepairService> logger,
            IHostEnvironment env)
        {
            _sp = sp;
            _logger = logger;
            _env = env;
            _tempBackupDir = Path.Combine(env.ContentRootPath, "TempBackup");
            Directory.CreateDirectory(_tempBackupDir);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[PathRepair] Servicio iniciado");
            
            // Esperar 10 segundos antes de comenzar (dar tiempo a que el servidor arranque)
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            
            try
            {
                await RepairAllTicketsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PathRepair] Error durante reparación masiva");
            }
            
            _logger.LogInformation("[PathRepair] Proceso completado. Servicio detenido.");
        }

        private async Task RepairAllTicketsAsync(CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
            var pathStrategy = scope.ServiceProvider.GetRequiredService<IBusinessFilePathStrategy>();
            
            var uploadsRoot = Path.Combine(_env.ContentRootPath, "Uploads");
            
            // Obtener todos los tickets
            var tickets = await db.ExpenseTickets
                .OrderBy(t => t.Id)
                .ToListAsync(ct);
            
            _logger.LogInformation("[PathRepair] Procesando {Count} tickets", tickets.Count);
            
            int processed = 0;
            int found = 0;
            int moved = 0;
            int errors = 0;
            
            foreach (var ticket in tickets)
            {
                if (ct.IsCancellationRequested)
                    break;
                
                try
                {
                    processed++;
                    _logger.LogInformation("[PathRepair] Ticket {Id}/{Total}: Procesando...", 
                        ticket.Id, tickets.Count);
                    
                    // PASO 1: Buscar el archivo físico
                    string? actualPath = await FindTicketFileAsync(ticket, uploadsRoot);
                    
                    if (actualPath == null)
                    {
                        _logger.LogWarning("[PathRepair] Ticket {Id}: Archivo no encontrado en ninguna ubicación", 
                            ticket.Id);
                        errors++;
                        continue;
                    }
                    
                    found++;
                    _logger.LogInformation("[PathRepair] Ticket {Id}: Archivo encontrado en {Path}", 
                        ticket.Id, actualPath.Replace(uploadsRoot, ""));
                    
                    // PASO 2: Extraer fecha del análisis
                    DateTime? purchaseDate = ExtractPurchaseDateFromAnalysis(ticket);
                    
                    if (purchaseDate.HasValue)
                    {
                        _logger.LogInformation("[PathRepair] Ticket {Id}: Fecha extraída del análisis: {Date}", 
                            ticket.Id, purchaseDate.Value.ToString("yyyy-MM-dd"));
                        ticket.PurchaseDate = purchaseDate.Value;
                        ticket.ExpenseYear = purchaseDate.Value.Year;
                        ticket.ExpenseMonth = purchaseDate.Value.Month;
                    }
                    else
                    {
                        _logger.LogInformation("[PathRepair] Ticket {Id}: No se pudo extraer fecha, usando valores por defecto", 
                            ticket.Id);
                        // Usar fecha de subida como fallback
                        ticket.PurchaseDate = null;
                        ticket.ExpenseYear = ticket.ExpenseYear ?? ticket.UploadedAt.Year;
                        ticket.ExpenseMonth = ticket.ExpenseMonth ?? ticket.UploadedAt.Month;
                    }
                    
                    // PASO 3: Calcular ruta de destino correcta
                    var companySlug = ticket.CompanySlug ?? 
                        (!string.IsNullOrWhiteSpace(ticket.CompanyName) 
                            ? ToSlug(ticket.CompanyName) 
                            : "default");
                    
                    ticket.CompanySlug = companySlug;
                    
                    var (dirRel, fileName) = pathStrategy.Generate(
                        companySlug,
                        ticket.ExpenseYear ?? DateTime.UtcNow.Year,
                        ticket.ExpenseMonth ?? DateTime.UtcNow.Month,
                        ticket.PublicId.ToString("N"),
                        ticket.FileName ?? Path.GetFileName(actualPath)
                    );
                    
                    var targetRelPath = Path.Combine(dirRel, fileName).Replace(Path.DirectorySeparatorChar, '/');
                    var targetPhysicalPath = Path.Combine(uploadsRoot, dirRel, fileName);
                    
                    // PASO 4: Verificar si necesita mover
                    var actualRelPath = actualPath.Replace(uploadsRoot, "").TrimStart('\\', '/').Replace('\\', '/');
                    
                    if (actualRelPath.Equals(targetRelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[PathRepair] Ticket {Id}: Ya está en ubicación correcta", ticket.Id);
                        
                        // Actualizar BD por si acaso
                        ticket.RelativePath = targetRelPath;
                        ticket.FileUrl = $"/Uploads/{targetRelPath}";
                        ticket.FileHash = ComputeFileHash(actualPath);
                        
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                    
                    // PASO 5: Mover archivo con backup temporal
                    bool moveSuccess = await MoveFileWithBackupAsync(
                        actualPath, 
                        targetPhysicalPath, 
                        ticket.Id,
                        ct);
                    
                    if (!moveSuccess)
                    {
                        _logger.LogError("[PathRepair] Ticket {Id}: Error al mover archivo", ticket.Id);
                        errors++;
                        continue;
                    }
                    
                    moved++;
                    
                    // PASO 6: Actualizar registro
                    ticket.FileName = fileName;
                    ticket.RelativePath = targetRelPath;
                    ticket.FileUrl = $"/Uploads/{targetRelPath}";
                    ticket.FileHash = ComputeFileHash(targetPhysicalPath);
                    ticket.FileSizeBytes = new FileInfo(targetPhysicalPath).Length;
                    
                    await db.SaveChangesAsync(ct);
                    
                    _logger.LogInformation("[PathRepair] Ticket {Id}: ? Movido exitosamente a {Path}", 
                        ticket.Id, targetRelPath);
                    
                    // PASO 7: Mover archivo de análisis si existe
                    await MoveAnalysisFileAsync(ticket, actualPath, targetPhysicalPath, uploadsRoot, db, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[PathRepair] Error procesando ticket {Id}", ticket.Id);
                    errors++;
                }
            }
            
            _logger.LogInformation(
                "[PathRepair] Resumen: {Processed} procesados, {Found} encontrados, {Moved} movidos, {Errors} errores",
                processed, found, moved, errors);
        }

        /// <summary>
        /// Busca el archivo físico del ticket. Primero en la ruta registrada, 
        /// luego recursivamente en Uploads.
        /// </summary>
        private async Task<string?> FindTicketFileAsync(ExpenseTicket ticket, string uploadsRoot)
        {
            // 1. Verificar ruta registrada
            if (!string.IsNullOrWhiteSpace(ticket.RelativePath))
            {
                var registeredPath = Path.Combine(uploadsRoot, ticket.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(registeredPath))
                {
                    return registeredPath;
                }
            }
            
            // 2. Verificar con FileName directamente en Uploads (legacy)
            if (!string.IsNullOrWhiteSpace(ticket.FileName))
            {
                var legacyPath = Path.Combine(uploadsRoot, ticket.FileName);
                if (File.Exists(legacyPath))
                {
                    return legacyPath;
                }
            }
            
            // 3. Búsqueda recursiva por nombre de archivo
            if (!string.IsNullOrWhiteSpace(ticket.FileName))
            {
                _logger.LogInformation("[PathRepair] Ticket {Id}: Buscando recursivamente {FileName}...", 
                    ticket.Id, ticket.FileName);
                
                var foundFiles = Directory.GetFiles(uploadsRoot, ticket.FileName, SearchOption.AllDirectories);
                if (foundFiles.Length > 0)
                {
                    return foundFiles[0]; // Tomar el primero encontrado
                }
            }
            
            // 4. Búsqueda por hash si está disponible
            if (!string.IsNullOrWhiteSpace(ticket.FileHash))
            {
                _logger.LogInformation("[PathRepair] Ticket {Id}: Buscando por hash...", ticket.Id);
                
                var allFiles = Directory.GetFiles(uploadsRoot, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsImageFile(f));
                
                foreach (var file in allFiles)
                {
                    try
                    {
                        var hash = ComputeFileHash(file);
                        if (hash.Equals(ticket.FileHash, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("[PathRepair] Ticket {Id}: Encontrado por hash en {Path}", 
                                ticket.Id, file);
                            return file;
                        }
                    }
                    catch { }
                }
            }
            
            return null;
        }

        /// <summary>
        /// Extrae la fecha de compra del JSON de análisis.
        /// </summary>
        private DateTime? ExtractPurchaseDateFromAnalysis(ExpenseTicket ticket)
        {
            if (string.IsNullOrWhiteSpace(ticket.AnalysisJson))
                return null;
            
            try
            {
                using var doc = JsonDocument.Parse(ticket.AnalysisJson);
                var root = doc.RootElement;
                
                // Buscar en varios campos posibles
                string[] dateFields = 
                { 
                    "Date", "date", 
                    "TicketDate", "ticketDate", 
                    "Fecha", "fecha",
                    "PurchaseDate", "purchaseDate",
                    "TransactionDate", "transactionDate"
                };
                
                // Primero buscar en "Summary" si existe
                if (root.TryGetProperty("Summary", out var summary))
                {
                    foreach (var field in dateFields)
                    {
                        if (summary.TryGetProperty(field, out var dateEl))
                        {
                            var dateStr = dateEl.GetString();
                            if (!string.IsNullOrWhiteSpace(dateStr) && 
                                DateTime.TryParse(dateStr, out var parsedDate))
                            {
                                return parsedDate;
                            }
                        }
                    }
                }
                
                // Luego buscar en raíz
                foreach (var field in dateFields)
                {
                    if (root.TryGetProperty(field, out var dateEl))
                    {
                        var dateStr = dateEl.GetString();
                        if (!string.IsNullOrWhiteSpace(dateStr) && 
                            DateTime.TryParse(dateStr, out var parsedDate))
                        {
                            return parsedDate;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PathRepair] Error extrayendo fecha del análisis del ticket {Id}", 
                    ticket.Id);
            }
            
            return null;
        }

        /// <summary>
        /// Mueve un archivo con backup temporal por si falla el proceso.
        /// </summary>
        private async Task<bool> MoveFileWithBackupAsync(
            string sourcePath, 
            string targetPath, 
            int ticketId,
            CancellationToken ct)
        {
            string? backupPath = null;
            
            try
            {
                // 1. Crear backup temporal
                var backupFileName = $"ticket_{ticketId}_{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(sourcePath)}";
                backupPath = Path.Combine(_tempBackupDir, backupFileName);
                
                File.Copy(sourcePath, backupPath, overwrite: true);
                _logger.LogInformation("[PathRepair] Ticket {Id}: Backup creado en {Path}", 
                    ticketId, backupPath);
                
                // 2. Crear directorio de destino
                var targetDir = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetDir);
                
                // 3. Si existe archivo en destino, eliminarlo (posible placeholder)
                if (File.Exists(targetPath))
                {
                    _logger.LogWarning("[PathRepair] Ticket {Id}: Archivo destino ya existe, sobrescribiendo", 
                        ticketId);
                    File.Delete(targetPath);
                }
                
                // 4. Mover archivo
                File.Move(sourcePath, targetPath, overwrite: false);
                
                // 5. Verificar que el movimiento fue exitoso
                if (!File.Exists(targetPath))
                {
                    _logger.LogError("[PathRepair] Ticket {Id}: Archivo no existe en destino después de mover", 
                        ticketId);
                    
                    // Restaurar desde backup
                    File.Copy(backupPath, sourcePath, overwrite: true);
                    return false;
                }
                
                // 6. Eliminar archivo original del directorio viejo (si quedó algo)
                if (File.Exists(sourcePath))
                {
                    File.Delete(sourcePath);
                }
                
                // 7. Mantener backup temporal por ahora (se limpiará después)
                _logger.LogInformation("[PathRepair] Ticket {Id}: Backup temporal mantenido en {Path}", 
                    ticketId, backupPath);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PathRepair] Error moviendo archivo del ticket {Id}", ticketId);
                
                // Intentar restaurar desde backup si existe
                if (backupPath != null && File.Exists(backupPath) && !File.Exists(sourcePath))
                {
                    try
                    {
                        File.Copy(backupPath, sourcePath, overwrite: true);
                        _logger.LogInformation("[PathRepair] Ticket {Id}: Restaurado desde backup", ticketId);
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.LogError(restoreEx, "[PathRepair] Error restaurando desde backup ticket {Id}", 
                            ticketId);
                    }
                }
                
                return false;
            }
        }

        /// <summary>
        /// Mueve el archivo de análisis markdown junto con el ticket.
        /// </summary>
        private async Task MoveAnalysisFileAsync(
            ExpenseTicket ticket,
            string oldTicketPath,
            string newTicketPath,
            string uploadsRoot,
            GesaiconDbContext db,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
                return;
            
            try
            {
                string? oldAnalysisPath = null;
                
                // Buscar archivo de análisis
                if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileUrl))
                {
                    var relPath = ticket.AnalysisFileUrl.Replace("/Uploads/", "").Replace('/', Path.DirectorySeparatorChar);
                    oldAnalysisPath = Path.Combine(uploadsRoot, relPath);
                }
                else
                {
                    // Legacy: mismo directorio que el ticket viejo
                    var oldDir = Path.GetDirectoryName(oldTicketPath)!;
                    oldAnalysisPath = Path.Combine(oldDir, ticket.AnalysisFileName);
                }
                
                if (!File.Exists(oldAnalysisPath))
                {
                    _logger.LogInformation("[PathRepair] Ticket {Id}: Archivo análisis no encontrado, omitiendo", 
                        ticket.Id);
                    return;
                }
                
                // Mover a la misma carpeta que el nuevo ticket
                var newDir = Path.GetDirectoryName(newTicketPath)!;
                var newAnalysisPath = Path.Combine(newDir, ticket.AnalysisFileName);
                var newAnalysisRelPath = newAnalysisPath.Replace(uploadsRoot, "")
                    .TrimStart(Path.DirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                
                if (File.Exists(newAnalysisPath))
                    File.Delete(newAnalysisPath);
                
                File.Move(oldAnalysisPath, newAnalysisPath, overwrite: false);
                
                ticket.AnalysisFileUrl = $"/Uploads/{newAnalysisRelPath}";
                await db.SaveChangesAsync(ct);
                
                _logger.LogInformation("[PathRepair] Ticket {Id}: Archivo análisis movido a {Path}", 
                    ticket.Id, newAnalysisRelPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PathRepair] Error moviendo archivo análisis del ticket {Id}", 
                    ticket.Id);
            }
        }

        private static string ComputeFileHash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        private static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";
        }

        private static string ToSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "default";

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
            
            return string.IsNullOrEmpty(text) ? "default" : text;
        }
    }
}
