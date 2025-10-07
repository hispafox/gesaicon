# ?? Guía Rápida: Reparación Automática de Tickets

## ? Pasos para Ejecutar

### 1. Aplicar Migración de Base de Datos

```bash
cd Gesaicon.Api
dotnet ef database update
```

Esto añade el campo `PurchaseDate` a la tabla `ExpenseTickets`.

### 2. Iniciar la Aplicación

```bash
dotnet run
```

El servicio `TicketPathRepairService` se ejecutará automáticamente **10 segundos después del inicio**.

### 3. Monitorear Logs

Observa la consola para ver el progreso:

```
[19:20:10 INF] [PathRepair] Servicio iniciado
[19:20:20 INF] [PathRepair] Procesando 97 tickets

[19:20:21 INF] [PathRepair] Ticket 1/97: Procesando...
[19:20:21 INF] [PathRepair] Ticket 1: Archivo encontrado en /IMG_0001.jpg
[19:20:21 INF] [PathRepair] Ticket 1: Fecha extraída del análisis: 2025-10-06
[19:20:21 INF] [PathRepair] Ticket 1: Backup creado en TempBackup/ticket_1_20251007192021.jpg
[19:20:22 INF] [PathRepair] Ticket 1: ? Movido exitosamente a empresas/mcdonalds/2025/10/abc123_IMG_0001.jpg
[19:20:22 INF] [PathRepair] Ticket 1: Archivo análisis movido a empresas/mcdonalds/2025/10/abc123-analysis.md

...

[19:25:30 INF] [PathRepair] Resumen: 97 procesados, 95 encontrados, 42 movidos, 2 errores
[19:25:30 INF] [PathRepair] Proceso completado. Servicio detenido.
```

## ?? Qué Hace el Servicio

### Para Cada Ticket:

1. **Busca el archivo**:
   - Primero en la ruta registrada (`RelativePath`)
   - Luego en `Uploads/{FileName}` (legacy)
   - Búsqueda recursiva en toda la carpeta `Uploads/`
   - Si todo falla, busca por hash SHA256

2. **Extrae la fecha real** del campo `AnalysisJson`:
   - Busca campos: `Date`, `TicketDate`, `PurchaseDate`, etc.
   - Actualiza `PurchaseDate`, `ExpenseYear`, `ExpenseMonth`
   - Si no encuentra fecha, usa `UploadedAt`

3. **Calcula la ruta correcta**:
   - Usa `CompanySlug` del análisis
   - Estructura: `empresas/{slug}/{year}/{month}/{publicId}_{filename}`

4. **Mueve el archivo** con seguridad:
   - Crea backup en `TempBackup/`
   - Mueve archivo a destino
   - Verifica que llegó correctamente
   - Mantiene backup temporal

5. **Actualiza el registro** en BD:
   - `PurchaseDate` con fecha real
   - `RelativePath`, `FileUrl` con rutas correctas
   - `FileHash` recalculado
   - `FileSizeBytes` actualizado

6. **Mueve archivo de análisis** (si existe):
   - Del mismo directorio origen
   - Al mismo directorio destino que el ticket

## ?? Verificar Resultados

### Comprobar Base de Datos

```sql
SELECT 
    Id,
    FileName,
    CompanySlug,
    ExpenseYear,
    ExpenseMonth,
    PurchaseDate,
    RelativePath,
    FileUrl
FROM ExpenseTickets
ORDER BY Id;
```

### Comprobar Estructura de Archivos

```
Gesaicon.Api/
??? Uploads/
?   ??? empresas/
?       ??? mcdonalds/
?       ?   ??? 2025/
?       ?       ??? 10/
?       ?           ??? abc123_IMG_001.jpg
?       ?           ??? abc123-analysis.md
?       ??? carrefour/
?       ?   ??? 2025/
?       ?       ??? 09/
?       ?           ??? def456_ticket.png
?       ??? default/
?           ??? 0000/
?               ??? 00/
?                   ??? (tickets sin análisis)
??? TempBackup/
    ??? ticket_1_20251007192021.jpg
    ??? ticket_2_20251007192022.jpg
    ??? ...
```

### Verificar en la UI

1. Abre la aplicación Blazor
2. Ve a la lista de tickets
3. Comprueba que las imágenes se cargan correctamente
4. Verifica que los análisis están disponibles

## ?? Errores Comunes

### "Archivo no encontrado en ninguna ubicación"

**Causa**: El archivo físico no existe o se movió manualmente.

**Solución**: 
- Verifica que el archivo existe en `Uploads/`
- Revisa el nombre en `FileName` de la BD
- Considera re-subir el archivo

### "Error al mover archivo"

**Causa**: Permisos insuficientes o archivo en uso.

**Solución**:
- Ejecuta como administrador
- Cierra programas que puedan tener archivos abiertos
- Revisa que el directorio `Uploads/` tiene permisos de escritura

### "Error extrayendo fecha del análisis"

**Causa**: El JSON de análisis no tiene fecha o está corrupto.

**Solución**: 
- El servicio usa `UploadedAt` como fallback automáticamente
- Considera re-procesar el ticket con análisis nuevo

## ?? Limpieza Post-Ejecución

### 1. Verificar que Todo Funciona

Espera 24-48 horas después de la ejecución y verifica:
- ? Todas las imágenes se cargan correctamente
- ? Los análisis están disponibles
- ? No hay errores en la UI

### 2. Limpiar Backups Temporales

**?? IMPORTANTE**: Solo después de verificar que todo funciona:

```bash
cd Gesaicon.Api
Remove-Item TempBackup\* -Force
```

O elimina manualmente la carpeta `TempBackup/`.

### 3. Desactivar el Servicio (Opcional)

Si no quieres que se ejecute cada vez que inicias la app:

En `Program.cs`, comenta o elimina:

```csharp
// builder.Services.AddHostedService<TicketPathRepairService>();
```

## ?? Re-ejecutar el Servicio

Si necesitas ejecutarlo de nuevo:

1. Asegúrate de que existe la carpeta `TempBackup/` o créala
2. Reinicia la aplicación: `dotnet run`
3. El servicio se ejecutará automáticamente

## ?? Nuevo Endpoint: PurchaseDate en API

El campo `PurchaseDate` está disponible en todos los endpoints:

### GET /api/tickets

```json
{
  "total": 97,
  "items": [
    {
      "id": 1,
      "publicId": "abc123...",
      "companySlug": "mcdonalds",
      "expenseYear": 2025,
      "expenseMonth": 10,
      "purchaseDate": "2025-10-06T00:00:00Z",  // ? NUEVO
      "fileName": "abc123_IMG_001.jpg",
      "fileUrl": "/Uploads/empresas/mcdonalds/2025/10/abc123_IMG_001.jpg",
      "relativePath": "empresas/mcdonalds/2025/10/abc123_IMG_001.jpg",
      "amount": 15.50,
      "companyName": "McDonald's",
      "category": "Restaurant"
    }
  ]
}
```

### Filtrar por Fecha de Compra

En el futuro puedes añadir filtros:

```csharp
[HttpGet]
public async Task<IActionResult> GetAll(
    [FromQuery] DateTime? purchaseDateFrom = null,
    [FromQuery] DateTime? purchaseDateTo = null)
{
    var q = _db.ExpenseTickets.AsNoTracking();
    
    if (purchaseDateFrom.HasValue)
        q = q.Where(t => t.PurchaseDate >= purchaseDateFrom.Value);
    
    if (purchaseDateTo.HasValue)
        q = q.Where(t => t.PurchaseDate <= purchaseDateTo.Value);
    
    // ...
}
```

## ?? Casos de Uso

### Caso 1: Ticket con Análisis Completo

```
Input:
- FileName: "IMG_001.jpg"
- RelativePath: null (legacy)
- AnalysisJson: { "Summary": { "Date": "2025-10-06", "Company": "McDonald's" } }
- ExpenseYear: null
- ExpenseMonth: null

Output:
- FileName: "abc123_IMG_001.jpg"
- RelativePath: "empresas/mcdonalds/2025/10/abc123_IMG_001.jpg"
- PurchaseDate: 2025-10-06
- ExpenseYear: 2025
- ExpenseMonth: 10
- CompanySlug: "mcdonalds"
```

### Caso 2: Ticket Sin Fecha en Análisis

```
Input:
- FileName: "ticket.png"
- AnalysisJson: { "Summary": { "Amount": 25.50 } } // sin fecha
- UploadedAt: 2025-10-06 23:10:00

Output:
- PurchaseDate: null (no extraída)
- ExpenseYear: 2025 (desde UploadedAt)
- ExpenseMonth: 10 (desde UploadedAt)
```

### Caso 3: Ticket Ya en Ubicación Correcta

```
Input:
- RelativePath: "empresas/carrefour/2025/09/xyz789_ticket.jpg"
- Archivo existe en esa ubicación

Output:
- No se mueve
- Solo actualiza FileHash y FileSizeBytes
- Log: "Ya está en ubicación correcta"
```

## ?? Documentación Adicional

- **[PATH_REPAIR_README.md](./PATH_REPAIR_README.md)**: Documentación técnica completa
- **[PREVIEW_STRUCTURE.md](./PREVIEW_STRUCTURE.md)**: Estructura de carpetas esperada
- **[AUTO_FIX_README.md](./AUTO_FIX_README.md)**: Servicio LegacyTicketFixService
- **[MIGRATION_GUIDE.md](./MIGRATION_GUIDE.md)**: Guía de migración manual

## ?? Soporte

Si encuentras problemas:

1. Revisa los logs en consola
2. Verifica la carpeta `TempBackup/` para restaurar archivos
3. Comprueba permisos de archivos y carpetas
4. Revisa que la BD tiene el campo `PurchaseDate`

**Logs detallados**: Los logs están en `Serilog` configurado en `appsettings.json`.
