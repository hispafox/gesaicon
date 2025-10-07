using Gesaicon.Api.Data;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Gesaicon.Api.Diagnostics;
using System.Text.RegularExpressions;

namespace Gesaicon.Api.Services;

/// <summary>
/// Reprocesa / migra tickets legacy siguiendo el guion indicado por el usuario.
/// Pasos:
/// 1) Recorre TODOS los registros de la tabla ExpenseTickets
/// 2) Comprueba la ruta donde el registro dice que está (RelativePath / FileName)
/// 3) Si no lo encuentra intenta localizarlo recursivamente en Uploads
/// 4) Una vez encontrado obtiene la fecha (JSON -> Markdown -> archivo MD -> ExpenseYear/Month). Si NO tiene fecha lo deja en default (carpeta 0000/00)
/// 5) Mueve el archivo a destino usando primero una copia temporal (rollback seguro) y luego elimina el original. Igual para el .md
/// 6) Comprueba que la ruta registrada en DB coincide con la ubicación física del archivo (y del markdown si existe)
/// </summary>
public sealed class LegacyTicketFixService : BackgroundService
{
    private readonly ILogger<LegacyTicketFixService> _logger;
    private readonly IServiceProvider _sp;
    private readonly IHostEnvironment _env;
    private readonly string _defaultCompanySlug;
    private readonly bool _forceReprocessAll; // ya no se usa para filtrar, pero mantenemos compatibilidad config
    private readonly BackgroundStatusStore _status;

    private const int WarmupSeconds = 2;
    private const string TempDirName = "__legacyfix_tmp";

    public LegacyTicketFixService(
        ILogger<LegacyTicketFixService> logger,
        IServiceProvider sp,
        IHostEnvironment env,
        IOptions<FileIngestionOptions> ingestionOptions,
        BackgroundStatusStore status)
    {
        _logger = logger;
        _sp = sp;
        _env = env;
        _defaultCompanySlug = ingestionOptions.Value.DefaultCompanySlug;
        _forceReprocessAll = ingestionOptions.Value.ForceReprocessAll;
        _status = status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_env.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
        {
            _status.Update("LegacyFix", "Saltado (Testing)", running: false);
            return;
        }

        _status.Update("LegacyFix", "Iniciando", running: true);
        _logger.LogInformation("[LegacyFix] Iniciando reproceso (ForceReprocessAll={Force})", _forceReprocessAll);
        try { await Task.Delay(TimeSpan.FromSeconds(WarmupSeconds), stoppingToken); } catch { }
        await RunAsync(stoppingToken);
        _status.Update("LegacyFix", "Finalizado", running: false);
        _logger.LogInformation("[LegacyFix] Finalizado");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
        var pathStrategy = scope.ServiceProvider.GetRequiredService<IBusinessFilePathStrategy>();
        var uploadsRoot = Path.Combine(_env.ContentRootPath, "Uploads");
        Directory.CreateDirectory(uploadsRoot);
        var tempRoot = Path.Combine(uploadsRoot, TempDirName);
        Directory.CreateDirectory(tempRoot);

        var tickets = await db.ExpenseTickets.AsNoTracking().ToListAsync(ct);
        if (tickets.Count == 0) { _logger.LogInformation("[LegacyFix] 0 tickets"); return; }

        // Index único de todos los archivos para búsqueda recursiva
        var allFiles = Directory.EnumerateFiles(uploadsRoot, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + TempDirName + Path.DirectorySeparatorChar))
            .ToList();
        _logger.LogInformation("[LegacyFix] Indexados {Count} archivos físicos", allFiles.Count);

        int processed = 0, moved = 0, skipped = 0, errors = 0, missing = 0, noDate = 0, verifiedOk = 0;

        foreach (var snapshot in tickets)
        {
            if (ct.IsCancellationRequested) break;
            processed++;
            _status.Update("LegacyFix", $"Ticket {snapshot.Id}", processedDelta: 1);
            try
            {
                // 2+3: localizar archivo principal
                var located = LocateFile(snapshot, uploadsRoot, allFiles);
                if (located == null)
                {
                    missing++;
                    _logger.LogWarning("[LegacyFix] Ticket {Id}: archivo no encontrado. RelativePath={Rel} FileName={File}", snapshot.Id, snapshot.RelativePath, snapshot.FileName);
                    continue;
                }
                var (currentPhys, originalFileName, currentRel) = located.Value;

                // 4: fecha real
                var (date, dateOrigin) = ExtractRealDate(snapshot, uploadsRoot);
                bool hasDate = date.HasValue;
                if (!hasDate)
                {
                    noDate++;
                    // carpeta default/0000/00
                }

                // Determinar destino
                var companySlug = hasDate && !string.IsNullOrWhiteSpace(snapshot.CompanyName)
                    ? ToSlug(snapshot.CompanyName!)
                    : _defaultCompanySlug; // sin fecha -> default

                int year = hasDate ? date!.Value.Year : 0;
                int month = hasDate ? date!.Value.Month : 0;

                // Cargar entidad trackeada para actualizar
                var ticket = await db.ExpenseTickets.FirstAsync(t => t.Id == snapshot.Id, ct);
                var ticketIdStr = ticket.PublicId.ToString("N");

                var (targetDirRel, targetFileName) = pathStrategy.Generate(companySlug, year, month, ticketIdStr, originalFileName);
                var targetPhysDir = Path.Combine(uploadsRoot, targetDirRel);
                var targetPhys = Path.Combine(targetPhysDir, targetFileName);
                var targetRel = Path.Combine(targetDirRel, targetFileName).Replace(Path.DirectorySeparatorChar, '/');

                bool alreadyRightPlace = Path.GetFullPath(currentPhys) == Path.GetFullPath(targetPhys);

                if (!alreadyRightPlace)
                {
                    Directory.CreateDirectory(targetPhysDir);

                    // 5: mover usando temp
                    var tempName = ticketIdStr + "-tmp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(currentPhys);
                    var tempPhys = Path.Combine(tempRoot, tempName);
                    try
                    {
                        File.Copy(currentPhys, tempPhys, overwrite: true);
                        File.Move(tempPhys, targetPhys, overwrite: true);
                        if (File.Exists(currentPhys)) File.Delete(currentPhys);
                    }
                    catch (Exception moveEx)
                    {
                        _logger.LogError(moveEx, "[LegacyFix] Ticket {Id}: error moviendo archivo principal", ticket.Id);
                        if (File.Exists(tempPhys)) { try { File.Delete(tempPhys); } catch { } }
                        errors++; continue;
                    }
                }

                // Mover markdown asociado si existe
                if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileUrl) && !string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
                {
                    try
                    {
                        var oldMdRel = ticket.AnalysisFileUrl.Replace("/Uploads/", "");
                        var oldMdPhys = Path.Combine(uploadsRoot, oldMdRel.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(oldMdPhys))
                        {
                            var newMdRel = Path.Combine(targetDirRel, ticket.AnalysisFileName).Replace(Path.DirectorySeparatorChar, '/');
                            var newMdPhys = Path.Combine(uploadsRoot, newMdRel.Replace('/', Path.DirectorySeparatorChar));
                            if (Path.GetFullPath(newMdPhys) != Path.GetFullPath(oldMdPhys))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(newMdPhys)!);
                                var tempMd = Path.Combine(tempRoot, ticketIdStr + "-mdtmp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(oldMdPhys));
                                File.Copy(oldMdPhys, tempMd, overwrite: true);
                                File.Move(tempMd, newMdPhys, overwrite: true);
                                if (File.Exists(oldMdPhys)) File.Delete(oldMdPhys);
                                ticket.AnalysisFileUrl = $"/Uploads/{newMdRel}";
                            }
                        }
                    }
                    catch (Exception mdEx)
                    {
                        _logger.LogError(mdEx, "[LegacyFix] Ticket {Id}: error moviendo markdown", ticket.Id);
                    }
                }

                // 6: actualizar registro + verificación
                ticket.CompanySlug = companySlug;
                ticket.ExpenseYear = hasDate ? year : null;
                ticket.ExpenseMonth = hasDate ? month : null;
                ticket.RelativePath = targetRel;
                ticket.FileUrl = $"/Uploads/{targetRel}";
                ticket.FileName ??= originalFileName;
                await db.SaveChangesAsync(ct);

                bool fileOk = File.Exists(targetPhys);
                bool mdOk = true;
                if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileUrl))
                {
                    var mdRel = ticket.AnalysisFileUrl.Replace("/Uploads/", "");
                    var mdPhys = Path.Combine(uploadsRoot, mdRel.Replace('/', Path.DirectorySeparatorChar));
                    mdOk = File.Exists(mdPhys);
                }

                if (fileOk && mdOk)
                {
                    moved += alreadyRightPlace ? 0 : 1;
                    verifiedOk++;
                    _logger.LogInformation("[LegacyFix] Ticket {Id} OK -> {Rel} origenFecha={Origen} fecha={Fecha}", ticket.Id, ticket.RelativePath, dateOrigin, hasDate ? date!.Value.ToString("yyyy-MM-dd") : "(sin)");
                }
                else
                {
                    errors++;
                    _logger.LogError("[LegacyFix] Ticket {Id} verificación fallida (fileOk={FileOk} mdOk={MdOk})", ticket.Id, fileOk, mdOk);
                }
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogError(ex, "[LegacyFix] Ticket {Id}: error general", snapshot.Id);
            }
        }

        _logger.LogInformation("[LegacyFix] Resumen: Total={Total} Movidos={Movidos} SinFecha(->default)={NoDate} NoEncontrado={Missing} Skip={Skip} OK={Ok} Errores={Errors}", tickets.Count, moved, noDate, missing, skipped, verifiedOk, errors);
        _status.Update("LegacyFix", $"Fin mov={moved} sinFecha={noDate} missing={missing} err={errors}", running: false);
    }

    private static (string physicalPath, string originalFileName, string? currentRel)? LocateFile(Models.ExpenseTicket t, string uploadsRoot, List<string> allFiles)
    {
        // a) RelativePath exacta
        if (!string.IsNullOrWhiteSpace(t.RelativePath))
        {
            var phys = Path.Combine(uploadsRoot, t.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(phys)) return (phys, t.FileName ?? Path.GetFileName(phys), t.RelativePath);
        }
        // b) FileName en cualquier carpeta
        if (!string.IsNullOrWhiteSpace(t.FileName))
        {
            var fn = t.FileName;
            var match = allFiles.FirstOrDefault(p => Path.GetFileName(p).Equals(fn, StringComparison.OrdinalIgnoreCase));
            if (match != null) return (match, fn, null);
        }
        // c) GUID sin guiones
        var guidN = t.PublicId.ToString("N");
        var matchGuid = allFiles.FirstOrDefault(p => Path.GetFileName(p).StartsWith(guidN, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("-analysis.md", StringComparison.OrdinalIgnoreCase));
        if (matchGuid != null) return (matchGuid, Path.GetFileName(matchGuid), null);
        // d) GUID con guiones contenido
        var guidHyphen = t.PublicId.ToString();
        var matchGuidH = allFiles.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Contains(guidHyphen, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("-analysis.md", StringComparison.OrdinalIgnoreCase));
        if (matchGuidH != null) return (matchGuidH, Path.GetFileName(matchGuidH), null);
        return null;
    }

    private static (DateTime? date, string origin) ExtractRealDate(Models.ExpenseTicket t, string uploadsRoot)
    {
        if (TryExtractDateFromAnalysis(t.AnalysisJson, out var dJson)) return (dJson, "json");
        if (!string.IsNullOrWhiteSpace(t.AnalysisMarkdown) && TryExtractDateFromMarkdown(t.AnalysisMarkdown!, out var dMd)) return (dMd, "markdown-inline");
        if (!string.IsNullOrWhiteSpace(t.AnalysisFileUrl))
        {
            var rel = t.AnalysisFileUrl.Replace("/Uploads/", "");
            var phys = Path.Combine(uploadsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(phys))
            {
                try { var txt = File.ReadAllText(phys); if (TryExtractDateFromMarkdown(txt, out var dMd2)) return (dMd2, "markdown-file"); } catch { }
            }
        }
        if (t.ExpenseYear.HasValue && t.ExpenseMonth.HasValue)
        {
            try { return (new DateTime(t.ExpenseYear.Value, t.ExpenseMonth.Value, 1), "stored"); } catch { }
        }
        return (null, "none");
    }

    private static string ToSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown";
        text = text.ToLowerInvariant();
        text = Regex.Replace(text.Normalize(System.Text.NormalizationForm.FormD), "[\\p{Mn}]", string.Empty);
        text = Regex.Replace(text, "[^a-z0-9\\s-]", "");
        text = Regex.Replace(text, "\\s+", " ").Trim();
        text = text.Replace(' ', '-');
        text = Regex.Replace(text, "-+", "-");
        text = text.Trim('-');
        if (text.Length > 50) text = text[..50].TrimEnd('-');
        return string.IsNullOrEmpty(text) ? "unknown" : text;
    }

    private static bool TryExtractDateFromAnalysis(string? analysisJson, out DateTime ticketDate)
    {
        ticketDate = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(analysisJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(analysisJson);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "date", "ticketDate", "fecha", "transactionDate" };
            DateTime? foundDate = null;
            bool Walk(System.Text.Json.JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object:
                        foreach (var prop in el.EnumerateObject())
                        {
                            if (wanted.Contains(prop.Name))
                            {
                                var s = prop.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(s) && TryParseFlexible(s, out var parsed)) { foundDate = parsed; return true; }
                            }
                            if (Walk(prop.Value)) return true;
                        }
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray()) if (Walk(item)) return true; break;
                }
                return false;
            }
            if (Walk(doc.RootElement) && foundDate.HasValue) { ticketDate = foundDate.Value; return true; }
        }
        catch { }
        return false;
    }

    private static bool TryExtractDateFromMarkdown(string markdown, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(markdown)) return false;
        var patterns = new[] { @"\\b\\d{4}-\\d{2}-\\d{2}\\b", @"\\b\\d{1,2}/\\d{1,2}/\\d{4}\\b", @"\\b\\d{1,2}-\\d{1,2}-\\d{4}\\b" };
        foreach (var p in patterns)
        {
            var m = Regex.Match(markdown, p);
            if (m.Success && TryParseFlexible(m.Value, out var parsed)) { date = parsed; return true; }
        }
        var meses = "enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|octubre|noviembre|diciembre";
        var rxLargo = new Regex(@$"\\b(\\d{{1,2}}) ({{{meses}}}) (\\d{{4}})\\b", RegexOptions.IgnoreCase);
        var m2 = rxLargo.Match(markdown);
        if (m2.Success)
        {
            try
            {
                var day = int.Parse(m2.Groups[1].Value);
                var monthName = m2.Groups[2].Value.ToLowerInvariant();
                var year = int.Parse(m2.Groups[3].Value);
                var month = Array.IndexOf(new[]{"enero","febrero","marzo","abril","mayo","junio","julio","agosto","septiembre","octubre","noviembre","diciembre"}, monthName) + 1;
                if (month > 0) { date = new DateTime(year, month, day); return true; }
            }
            catch { }
        }
        return false;
    }

    private static bool TryParseFlexible(string input, out DateTime dt)
    {
        string[] formats = {"yyyy-MM-dd","dd/MM/yyyy","d/M/yyyy","dd-MM-yyyy","d-M-yyyy","yyyy/MM/dd","yyyy.M.d","dd.MM.yyyy","d.MM.yyyy"};
        if (DateTime.TryParse(input, out dt)) return true;
        foreach (var f in formats)
            if (DateTime.TryParseExact(input, f, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return true;
        return false;
    }
}
