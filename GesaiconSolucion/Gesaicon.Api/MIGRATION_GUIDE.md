# Migración a Estructura de Carpetas por Empresa/Año/Mes

## Resumen de Cambios

Se ha implementado una nueva estructura de almacenamiento de archivos que organiza los tickets por empresa, año y mes:

```
Uploads/
??? empresas/
    ??? {empresaSlug}/
        ??? {yyyy}/
            ??? {MM}/
                ??? {ticketId}.ext
                ??? {ticketId}-analysis.md
```

### Ejemplo:
```
Uploads/empresas/acme/2025/01/a3f9c2e4d9ab4e2a8e9184b0ce0a1b2d.jpg
Uploads/empresas/acme/2025/01/a3f9c2e4d9ab4e2a8e9184b0ce0a1b2d-analysis.md
```

## Cambios en el Modelo

### ExpenseTicket - Nuevos campos:
- `CompanySlug` (string?): Slug de la empresa
- `ExpenseYear` (int?): Año del gasto
- `ExpenseMonth` (int?): Mes del gasto
- `RelativePath` (string?): Ruta relativa completa desde Uploads

## Servicios Nuevos

### IBusinessFilePathStrategy
Genera rutas según el patrón empresas/{slug}/{yyyy}/{MM}/{ticketId}.ext

### IFileStorageService
- `SaveTicketFileAsync`: Guarda archivos con la nueva estructura
- `GetPhysicalPath`: Convierte ruta relativa a física
- `FileExists`: Verifica existencia de archivo

### LegacyTicketFixService
**Corrección automática al arranque**: Este servicio se ejecuta automáticamente cuando la aplicación inicia y corrige:
- Tickets sin `RelativePath` (estructura legacy antigua)
- Tickets con `CompanySlug` incorrecto o `null`
- Mueve archivos físicamente a la nueva estructura
- Actualiza la base de datos automáticamente

**No requiere intervención manual**. La aplicación se autocorrige al arrancar.

## Compatibilidad con Archivos Legacy

El sistema mantiene **compatibilidad total** con archivos existentes:
- Si `RelativePath` está vacío, usa `FileName` (estructura antigua)
- Todos los servicios tienen fallback automático
- **Corrección automática al arranque** mediante `LegacyTicketFixService`
- No se requiere migración manual

## Pasos Necesarios

### 1. Crear y Ejecutar Migración de Base de Datos

```bash
cd Gesaicon.Api
dotnet ef migrations add AddCompanyAndPathFields
dotnet ef database update
```

### 2. Configuración

En `appsettings.json`, configurar la empresa por defecto:
```json
"FileIngestion": {
  "DefaultCompanySlug": "miempresa",
  ...
}
```

**¡Listo!** La aplicación corregirá automáticamente los tickets legacy al iniciar.

### 3. (Opcional) Migración Manual

Si prefieres ejecutar la migración manualmente antes del arranque:

**Prueba en seco (dry-run):**
```bash
dotnet run -- migrate-uploads --dry-run
```

**Migración real:**
```bash
dotnet run -- migrate-uploads
```

**Con empresa específica:**
```bash
dotnet run -- migrate-uploads --company-slug acme
```

**Corregir empresa en tickets ya migrados:**
```bash
# Prueba primero con dry-run
dotnet run -- migrate-uploads --company-slug acme --force-company --dry-run

# Ejecutar corrección real
dotnet run -- migrate-uploads --company-slug acme --force-company
```

#### Modos de Migración

1. **Modo Normal** (sin `--force-company`):
   - Solo procesa tickets sin `RelativePath` (tickets legacy)
   - Migra archivos de estructura antigua a nueva
   - Establece `CompanySlug`, `ExpenseYear`, `ExpenseMonth`

2. **Modo Force-Company** (con `--force-company`):
   - Procesa **todos** los tickets que no tienen el `CompanySlug` especificado
   - Útil para corregir empresa en tickets ya migrados
   - Mueve archivos físicamente si es necesario
   - Actualiza `CompanySlug` y recalcula `RelativePath`

## Ventajas de la Nueva Estructura

1. **Organización**: Miles de archivos organizados por empresa/año/mes
2. **Limpieza**: Borrar un mes completo = borrar carpeta
3. **Auditoría**: Fácil ubicar archivos por periodo
4. **Escalabilidad**: Preparado para migrar a Azure Blob Storage
5. **Sin colisiones**: Nombre de archivo = ticketId único
6. **Corrección automática**: No requiere intervención manual

## Extensiones Permitidas

- .jpg, .jpeg, .png, .gif, .webp (imágenes)
- .pdf, .txt (documentos)

## Endpoints API Actualizados

### GET /api/tickets
Devuelve tickets con nuevos campos:
```json
{
  "id": 1,
  "publicId": "...",
  "companySlug": "acme",
  "expenseYear": 2025,
  "expenseMonth": 1,
  "relativePath": "empresas/acme/2025/01/ticket.jpg",
  ...
}
```

### GET /api/tickets/{id}/analysis
Funciona con estructura nueva y legacy automáticamente.

## Notas Importantes

- **La corrección es automática** al iniciar la aplicación
- Configura `DefaultCompanySlug` en `appsettings.json` antes de arrancar
- **No elimines archivos antiguos** hasta confirmar que la migración fue exitosa
- Los nuevos archivos se guardan automáticamente con la nueva estructura
- `FileIngestion` usa `DefaultCompanySlug` para archivos automáticos

## Testing

Todos los tests existentes han sido actualizados para incluir mock de `IFileStorageService`.

## Migración a Azure Blob (futuro)

La clave blob será idéntica:
```
empresas/acme/2025/01/ticketId.jpg
```

Simplemente cambiar implementación de `IFileStorageService` sin tocar lógica de negocio.
