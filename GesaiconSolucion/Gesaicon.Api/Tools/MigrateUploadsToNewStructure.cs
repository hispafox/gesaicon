using System.Security.Cryptography;
using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gesaicon.Api.Tools;

/// <summary>
/// Herramienta para migrar archivos existentes de Uploads/ a empresas/{slug}/{yyyy}/{MM}/
/// USO: Ejecutar como comando dotnet run -- migrate-uploads [--dry-run] [--company-slug default] [--force-company]
/// </summary>
public static class MigrateUploadsToNewStructure
{
    public static async Task RunAsync(IServiceProvider sp, bool dryRun, string defaultCompanySlug, bool forceCompany, CancellationToken ct)
    {
        var logger = sp.GetRequiredService<ILogger<Program>>();
        var db = sp.GetRequiredService<GesaiconDbContext>();
        var storage = sp.GetRequiredService<IFileStorageService>();
        var strategy = sp.GetRequiredService<IBusinessFilePathStrategy>();
        var env = sp.GetRequiredService<IHostEnvironment>();

        var uploadsRoot = Path.Combine(env.ContentRootPath, "Uploads");

        logger.LogInformation("[Migración] Iniciando migración. DryRun={DryRun}, CompanySlug={Slug}, ForceCompany={Force}",
            dryRun, defaultCompanySlug, forceCompany);

        // Si forceCompany = true, también incluimos tickets ya migrados pero con empresa incorrecta
        var ticketsQuery = db.ExpenseTickets.AsQueryable();
        
        if (forceCompany)
        {
            // Re-migrar todos los tickets que no tienen CompanySlug correcto
            ticketsQuery = ticketsQuery.Where(t => t.CompanySlug == null || t.CompanySlug != defaultCompanySlug);
            logger.LogInformation("[Migración] Modo ForceCompany: corrigiendo empresa para todos los tickets sin CompanySlug='{Slug}'", defaultCompanySlug);
        }
        else
        {
            // Modo normal: solo tickets sin migrar
            ticketsQuery = ticketsQuery.Where(t => t.RelativePath == null || t.RelativePath == "");
        }

        var tickets = await ticketsQuery.ToListAsync(ct);

        logger.LogInformation("[Migración] {Count} tickets encontrados para procesar", tickets.Count);

        int migrated = 0, errors = 0, skipped = 0;

        foreach (var ticket in tickets)
        {
            try
            {
                // Inferir fecha de gasto desde UploadedAt si no existe
                var year = ticket.ExpenseYear ?? ticket.UploadedAt.Year;
                var month = ticket.ExpenseMonth ?? ticket.UploadedAt.Month;
                var companySlug = ticket.CompanySlug ?? defaultCompanySlug;
                
                // Si forceCompany está activo, forzar el slug especificado
                if (forceCompany)
                {
                    companySlug = defaultCompanySlug;
                }

                // Determinar ruta antigua del archivo
                string? oldPath = null;
                if (!string.IsNullOrWhiteSpace(ticket.RelativePath))
                {
                    // Ticket ya migrado, usar RelativePath
                    oldPath = Path.Combine(uploadsRoot, ticket.RelativePath.Replace('/', '\\'));
                }
                else if (!string.IsNullOrWhiteSpace(ticket.FileName))
                {
                    // Ticket legacy, usar FileName directamente en Uploads/
                    oldPath = Path.Combine(uploadsRoot, ticket.FileName);
                }

                if (oldPath == null || !File.Exists(oldPath))
                {
                    logger.LogWarning("[Migración] Ticket {Id}: archivo no encontrado {File}",
                        ticket.Id, oldPath ?? ticket.FileName ?? "???");
                    skipped++;
                    continue;
                }

                var ticketId = ticket.PublicId.ToString("N");
                var (dirRel, fileName) = strategy.Generate(companySlug, year, month, ticketId, ticket.FileName ?? Path.GetFileName(oldPath));
                var newRelativePath = Path.Combine(dirRel, fileName).Replace('\\', '/');
                var newPhysicalPath = Path.Combine(uploadsRoot, dirRel, fileName);

                // Normalizar rutas para comparación
                var normalizedOldPath = Path.GetFullPath(oldPath);
                var normalizedNewPath = Path.GetFullPath(newPhysicalPath);
                
                // Si ya está en el lugar correcto, solo actualizar BD si es necesario
                if (normalizedOldPath == normalizedNewPath)
                {
                    // Verificar si necesita actualizar la BD
                    var needsUpdate = ticket.CompanySlug != companySlug ||
                                     ticket.ExpenseYear != year ||
                                     ticket.ExpenseMonth != month ||
                                     ticket.RelativePath != newRelativePath;
                    
                    if (needsUpdate)
                    {
                        logger.LogInformation("[Migración] Ticket {Id}: ya está en ubicación correcta, actualizando BD", ticket.Id);
                        
                        if (!dryRun)
                        {
                            ticket.CompanySlug = companySlug;
                            ticket.ExpenseYear = year;
                            ticket.ExpenseMonth = month;
                            ticket.RelativePath = newRelativePath;
                            ticket.FileUrl = $"/Uploads/{newRelativePath}";
                        }
                        
                        migrated++;
                    }
                    else
                    {
                        logger.LogInformation("[Migración] Ticket {Id}: ya está correcto, omitiendo", ticket.Id);
                        skipped++;
                    }
                    continue;
                }

                logger.LogInformation("[Migración] Ticket {Id}: moviendo de {Old} -> {New}",
                    ticket.Id, ticket.RelativePath ?? Path.GetFileName(oldPath), newRelativePath);

                if (!dryRun)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
                    
                    // Si el archivo destino ya existe (posible duplicado), generar nombre único
                    if (File.Exists(newPhysicalPath))
                    {
                        logger.LogWarning("[Migración] Archivo destino ya existe, generando nombre único");
                        var uniqueName = $"{ticketId}_{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(fileName)}";
                        fileName = uniqueName;
                        newRelativePath = Path.Combine(dirRel, fileName).Replace('\\', '/');
                        newPhysicalPath = Path.Combine(uploadsRoot, dirRel, fileName);
                    }
                    
                    File.Move(oldPath, newPhysicalPath, overwrite: false);

                    ticket.CompanySlug = companySlug;
                    ticket.ExpenseYear = year;
                    ticket.ExpenseMonth = month;
                    ticket.RelativePath = newRelativePath;
                    ticket.FileUrl = $"/Uploads/{newRelativePath}";

                    // Migrar análisis markdown si existe
                    if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
                    {
                        string? oldAnalysisPath = null;
                        
                        if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileUrl))
                        {
                            // Extraer ruta relativa desde URL
                            var relPath = ticket.AnalysisFileUrl.Replace("/Uploads/", "").Replace('/', '\\');
                            oldAnalysisPath = Path.Combine(uploadsRoot, relPath);
                        }
                        else
                        {
                            // Legacy: archivo directamente en Uploads/
                            oldAnalysisPath = Path.Combine(uploadsRoot, ticket.AnalysisFileName);
                        }
                        
                        if (File.Exists(oldAnalysisPath))
                        {
                            var newAnalysisRelPath = Path.Combine(dirRel, ticket.AnalysisFileName).Replace('\\', '/');
                            var newAnalysisPath = Path.Combine(uploadsRoot, dirRel, ticket.AnalysisFileName);
                            
                            if (Path.GetFullPath(oldAnalysisPath) != Path.GetFullPath(newAnalysisPath))
                            {
                                File.Move(oldAnalysisPath, newAnalysisPath, overwrite: false);
                                ticket.AnalysisFileUrl = $"/Uploads/{newAnalysisRelPath}";
                                logger.LogInformation("[Migración] Análisis movido: {File}", newAnalysisRelPath);
                            }
                        }
                    }
                }

                migrated++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Migración] Error migrando ticket {Id}", ticket.Id);
                errors++;
            }
        }

        if (!dryRun)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("[Migración] Cambios guardados en BD");
        }

        logger.LogInformation("[Migración] Completada. Migrados={Migrated}, Omitidos={Skipped}, Errores={Errors}, DryRun={DryRun}",
            migrated, skipped, errors, dryRun);
    }
}
