# ?? Servicio de Reparación Automática de Rutas

## ?? Resumen

El `TicketPathRepairService` es un servicio de background que se ejecuta **una vez al inicio** de la aplicación para:

1. ? **Verificar y corregir rutas** de todos los tickets
2. ?? **Buscar archivos perdidos** recursivamente en `Uploads/`
3. ?? **Extraer fechas reales** del análisis JSON
4. ?? **Mover archivos** a la estructura correcta con backup
5. ?? **Actualizar registros** de BD con rutas y hash correctos

## ?? Problema que Resuelve

Algunos tickets pueden tener:
- ? Rutas incorrectas en la BD
- ? Archivos en ubicaciones legacy
- ? Fechas basadas en `ExpenseMonth`/`ExpenseYear` en lugar de la fecha real del ticket
- ? Archivos en carpetas placeholder `0000/00/`

## ?? Flujo de Reparación

### Paso 1: Buscar Archivo Físico

El servicio busca el archivo en este orden:

```csharp
1. ticket.RelativePath ? Path.Combine(uploadsRoot, ticket.RelativePath)
2. ticket.FileName ? Path.Combine(uploadsRoot, ticket.FileName) // Legacy
3. Búsqueda recursiva por nombre ? Directory.GetFiles(uploadsRoot, fileName, SearchOption.AllDirectories)
4. Búsqueda por hash SHA256 ? Escanea todos los archivos de imagen
```

### Paso 2: Extraer Fecha del Análisis

Busca la fecha en el campo `AnalysisJson` en estos campos:

```json
{
  "Summary": {
    "Date": "2025-10-06",
    "Amount": 15.50,
    "Company": "McDonald's"
  }
}
```

Campos buscados (en orden):
- `Date`, `date`
- `TicketDate`, `ticketDate`
- `Fecha`, `fecha`
- `PurchaseDate`, `purchaseDate`
- `TransactionDate`, `transactionDate`

### Paso 3: Calcular Ruta Correcta

Usa `BusinessFilePathStrategy` para generar la ruta:

```
empresas/{company-slug}/{year}/{month}/{publicId}_{originalFileName}
```

Ejemplo:
```
empresas/mcdonalds/2025/10/a1b2c3d4e5f6_{filename}.jpg
```

### Paso 4: Mover con Backup

**Antes de mover:**
1. ? Crea backup en `TempBackup/ticket_{id}_{timestamp}.{ext}`
2. ? Crea directorio destino si no existe
3. ? Elimina archivo destino si existe (placeholder)

**Durante el movimiento:**
1. ? Mueve archivo físico
2. ? Verifica que existe en destino
3. ? Elimina archivo origen si quedó

**Si falla:**
1. ? Restaura desde backup
2. ? Registra error en logs

### Paso 5: Actualizar Registro

```csharp
ticket.PurchaseDate = extractedDate;        // Nueva fecha extraída
ticket.ExpenseYear = extractedDate.Year;
ticket.ExpenseMonth = extractedDate.Month;
ticket.CompanySlug = "mcdonalds";
ticket.RelativePath = "empresas/mcdonalds/2025/10/abc123_file.jpg";
ticket.FileUrl = "/Uploads/empresas/mcdonalds/2025/10/abc123_file.jpg";
ticket.FileHash = "ABC123...";              // Hash recalculado
ticket.FileSizeBytes = 245678;
```

### Paso 6: Mover Archivo de Análisis

Si existe `AnalysisFileName`, también lo mueve:

```
Antes: Uploads/abc123-analysis.md
Después: Uploads/empresas/mcdonalds/2025/10/abc123-analysis.md
```

## ?? Logs Esperados

```
[PathRepair] Servicio iniciado
[PathRepair] Procesando 97 tickets

[PathRepair] Ticket 1/97: Procesando...
[PathRepair] Ticket 1: Archivo encontrado en /legacy/file.jpg
[PathRepair] Ticket 1: Fecha extraída del análisis: 2025-10-06
[PathRepair] Ticket 1: Backup creado en TempBackup/ticket_1_20251007192045.jpg
[PathRepair] Ticket 1: ? Movido exitosamente a empresas/mcdonalds/2025/10/abc123_file.jpg
[PathRepair] Ticket 1: Archivo análisis movido a empresas/mcdonalds/2025/10/abc123-analysis.md

[PathRepair] Ticket 2/97: Procesando...
[PathRepair] Ticket 2: Ya está en ubicación correcta

...

[PathRepair] Resumen: 97 procesados, 95 encontrados, 42 movidos, 2 errores
[PathRepair] Proceso completado. Servicio detenido.
```

## ?? Configuración

### Activación Automática

El servicio se registra automáticamente en `Program.cs`:

```csharp
builder.Services.AddHostedService<TicketPathRepairService>();
```

### Ejecución

- ? Se ejecuta **10 segundos después del inicio** de la aplicación
- ?? **Solo una vez** por ejecución
- ?? Se detiene automáticamente al finalizar

### Backup Temporal

Los backups se guardan en:
```
{ContentRootPath}/TempBackup/ticket_{id}_{timestamp}.{ext}
```

**?? Importante:** Los backups se mantienen hasta que se verifique que todo funciona correctamente. Limpiar manualmente después.

## ?? Testing

El servicio incluye búsqueda exhaustiva:

1. **Por ruta registrada** (más rápido)
2. **Por nombre de archivo en Uploads/** (legacy)
3. **Búsqueda recursiva** en toda la carpeta Uploads
4. **Por hash SHA256** (más lento pero 100% confiable)

## ?? Nuevo Campo: PurchaseDate

Se añadió el campo `PurchaseDate` (DateTime?) a `ExpenseTicket`:

```csharp
public DateTime? PurchaseDate { get; set; }
```

**Ventajas:**
- ? Fecha **real del ticket** extraída del análisis
- ? Independiente de `ExpenseMonth` y `ExpenseYear`
- ? Más precisa para reportes y filtros
- ? Nullable si no se pudo extraer

**Migración:**
```bash
dotnet ef migrations add AddPurchaseDateField
dotnet ef database update
```

## ?? Estructura Final Esperada

```
Uploads/
??? empresas/
?   ??? mcdonalds/
?   ?   ??? 2025/
?   ?       ??? 10/
?   ?           ??? abc123_IMG_001.jpg
?   ?           ??? abc123-analysis.md
?   ?           ??? def456_IMG_002.jpg
?   ?           ??? def456-analysis.md
?   ??? carrefour/
?   ?   ??? 2025/
?   ?       ??? 09/
?   ?           ??? xyz789_ticket.png
?   ??? default/
?       ??? 0000/
?           ??? 00/
?               ??? (tickets sin análisis)
??? TempBackup/
    ??? ticket_1_20251007192045.jpg
    ??? ticket_2_20251007192046.jpg
    ??? ...
```

## ? Checklist Post-Ejecución

Después de ejecutar el servicio:

1. ? Verificar logs en consola/archivo
2. ? Comprobar que todos los tickets tienen `RelativePath` correcto
3. ? Verificar que los archivos están en las carpetas correctas
4. ? Probar la carga de imágenes en la UI
5. ? Revisar la carpeta `TempBackup/` y limpiar si todo OK
6. ? Comprobar que `PurchaseDate` tiene valores donde sea posible

## ?? Desactivar el Servicio

Si necesitas desactivar el servicio temporalmente, comenta la línea en `Program.cs`:

```csharp
// builder.Services.AddHostedService<TicketPathRepairService>();
```

O elimina el servicio completamente después de la primera ejecución exitosa.

## ?? Servicios Relacionados

- `FolderIngestionService`: Ingesta de archivos desde carpeta
- `ScheduledBatchAnalysisService`: Análisis automático con IA
- `LegacyTicketFixService`: Corrección de estructura legacy
- `FileStorageService`: Gestión de almacenamiento de archivos
- `BusinessFilePathStrategy`: Estrategia de rutas por empresa/fecha
