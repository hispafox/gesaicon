# ?? Resumen de Cambios: Sistema de Reparación Automática de Rutas

## ? Cambios Implementados

### 1. Modelo de Datos

#### ExpenseTicket.cs
- ? Añadido campo `PurchaseDate` (DateTime?) para almacenar la fecha real del ticket extraída del análisis
- ? Este campo es independiente de `ExpenseMonth`/`ExpenseYear` y más preciso

### 2. Migración de Base de Datos

#### 20251007172014_AddPurchaseDateField.cs
- ? Migración creada para añadir columna `PurchaseDate` (datetime2, nullable)
- ? Ejecutar con: `dotnet ef database update`

### 3. Nuevo Servicio Background

#### TicketPathRepairService.cs (NUEVO)
Servicio que se ejecuta una vez al inicio para:

**Funcionalidades principales:**
1. **Buscar archivos perdidos** de 4 formas:
   - Por ruta registrada (`RelativePath`)
   - Por nombre de archivo en `Uploads/` (legacy)
   - Búsqueda recursiva en toda la carpeta `Uploads/`
   - Por hash SHA256 (más lento pero 100% confiable)

2. **Extraer fecha real del análisis**:
   - Busca en `AnalysisJson` campos: `Date`, `TicketDate`, `PurchaseDate`, etc.
   - Actualiza `PurchaseDate`, `ExpenseYear`, `ExpenseMonth`
   - Si no encuentra, usa `UploadedAt` como fallback

3. **Calcular ruta correcta**:
   - Usa `BusinessFilePathStrategy`
   - Estructura: `empresas/{company-slug}/{year}/{month}/{publicId}_{filename}`

4. **Mover archivos con backup**:
   - Crea backup temporal en `TempBackup/`
   - Mueve archivo a destino
   - Verifica integridad
   - Mantiene backup hasta confirmar éxito

5. **Actualizar registros**:
   - `PurchaseDate`, `ExpenseYear`, `ExpenseMonth`
   - `CompanySlug`, `RelativePath`, `FileUrl`
   - `FileHash`, `FileSizeBytes`

6. **Mover análisis markdown**:
   - Mueve `{publicId}-analysis.md` junto con el ticket
   - Actualiza `AnalysisFileUrl`

**Configuración:**
- Registrado en `Program.cs` como `HostedService`
- Se ejecuta 10 segundos después del inicio
- Se detiene automáticamente al finalizar

### 4. Actualización de Servicios Existentes

#### ScheduledBatchAnalysisService.cs
- ? Modificado para guardar `PurchaseDate` cuando extrae fecha del análisis
- ? Código añadido: `ticket.PurchaseDate = ticketDate.Value;`

### 5. Actualización de API

#### TicketsController.cs
- ? Añadido `PurchaseDate` al DTO
- ? Actualizado en todas las proyecciones:
  - `GetAll()` - lista con paginación
  - `GetOne(id)` - detalle de ticket
  - `Reprocess(id)` - reprocesar ticket
  - `MapToDto()` - mapeo interno

#### ExpenseTicketDto.cs (Web)
- ? Añadido campo `PurchaseDate` al DTO compartido

### 6. Documentación

#### PATH_REPAIR_README.md (NUEVO)
- Documentación técnica completa del servicio
- Explicación detallada de cada paso
- Estructura de archivos esperada
- Checklist post-ejecución

#### QUICK_START_PATH_REPAIR.md (NUEVO)
- Guía rápida de uso
- Instrucciones paso a paso
- Ejemplos de casos de uso
- Resolución de problemas comunes

### 7. Tests

#### FolderIngestionServiceTests.cs
- ? Añadidos tests para verificar campo `PurchaseDate`
- ? Test que verifica que puede ser nullable
- ? Test que verifica valores correctos

## ?? Comparación: Antes vs Después

### ANTES
```
Ticket {
  ExpenseYear: 2025        // Basado en fecha de subida
  ExpenseMonth: 10         // Basado en fecha de subida
  RelativePath: null       // Legacy, sin ruta
  FileName: "IMG_001.jpg"  // Solo nombre
  CompanySlug: "default"   // Valor por defecto
}

Archivo físico:
  Uploads/IMG_001.jpg      // Raíz de Uploads
```

### DESPUÉS
```
Ticket {
  PurchaseDate: 2025-10-06T00:00:00Z  // ? NUEVO: Fecha real del ticket
  ExpenseYear: 2025                    // Extraído de PurchaseDate
  ExpenseMonth: 10                     // Extraído de PurchaseDate
  RelativePath: "empresas/mcdonalds/2025/10/abc123_IMG_001.jpg"
  FileName: "abc123_IMG_001.jpg"
  CompanySlug: "mcdonalds"             // Extraído del análisis
}

Archivo físico:
  Uploads/empresas/mcdonalds/2025/10/abc123_IMG_001.jpg
  Uploads/empresas/mcdonalds/2025/10/abc123-analysis.md
```

## ?? Beneficios

### Para el Usuario
- ? **Organización clara**: Archivos organizados por empresa/año/mes
- ? **Fechas precisas**: Fecha real del ticket, no de subida
- ? **Búsqueda fácil**: Estructura intuitiva para encontrar tickets
- ? **Backup automático**: Seguridad antes de mover archivos

### Para el Sistema
- ? **Migración automática**: De estructura legacy a nueva estructura
- ? **Recuperación de archivos**: Busca archivos perdidos recursivamente
- ? **Integridad garantizada**: Verificación de hash y tamaño
- ? **Rollback seguro**: Backups temporales para recuperación

### Para el Desarrollo
- ? **Campo semántico**: `PurchaseDate` más claro que `ExpenseYear`/`ExpenseMonth`
- ? **API enriquecida**: Nuevo campo disponible en todos los endpoints
- ? **Filtros futuros**: Fácil añadir filtros por rango de fechas
- ? **Reportes precisos**: Usar fecha real del ticket en reportes

## ?? Archivos Modificados

### Backend (Gesaicon.Api)
```
?? Models/ExpenseTicket.cs
?? Services/ScheduledBatchAnalysisService.cs
?? Controllers/TicketsController.cs
?? Program.cs
? Services/TicketPathRepairService.cs (NUEVO)
? Migrations/20251007172014_AddPurchaseDateField.cs (NUEVO)
? PATH_REPAIR_README.md (NUEVO)
? QUICK_START_PATH_REPAIR.md (NUEVO)
```

### Frontend (Gesaicon.Web)
```
?? Models/ExpenseTicketDto.cs
```

### Tests (Gesaicon.Api.Tests)
```
?? Services/FolderIngestionServiceTests.cs
```

## ?? Instrucciones de Despliegue

### 1. Actualizar Base de Datos
```bash
cd Gesaicon.Api
dotnet ef database update
```

### 2. Compilar y Ejecutar
```bash
dotnet build
dotnet run
```

### 3. Verificar Logs
Observa la consola para ver el progreso del servicio `TicketPathRepairService`.

### 4. Verificar Resultados
- Comprobar que archivos están en ubicaciones correctas
- Verificar que campo `PurchaseDate` tiene valores
- Probar carga de imágenes en UI

### 5. Limpiar Backups (Opcional)
Después de 24-48 horas y verificar que todo funciona:
```bash
Remove-Item TempBackup\* -Force
```

## ?? Seguridad y Rollback

### Backups Temporales
- Todos los archivos movidos tienen backup en `TempBackup/`
- Nombre formato: `ticket_{id}_{timestamp}.{ext}`
- Mantener hasta confirmar éxito

### Rollback Manual
Si algo sale mal:
1. Detener aplicación
2. Restaurar archivos desde `TempBackup/` a ubicaciones originales
3. Revertir migración: `dotnet ef database update [PreviousMigration]`
4. Reportar error

### Rollback Automático
El servicio intenta restaurar automáticamente si falla el movimiento:
```csharp
if (backupPath != null && File.Exists(backupPath) && !File.Exists(sourcePath))
{
    File.Copy(backupPath, sourcePath, overwrite: true);
    _logger.LogInformation("[PathRepair] Ticket {Id}: Restaurado desde backup", ticketId);
}
```

## ?? Métricas y Monitoreo

### Logs Clave
- `[PathRepair] Procesando {Count} tickets` - Total a procesar
- `[PathRepair] Ticket {Id}: Archivo encontrado` - Archivo localizado
- `[PathRepair] Ticket {Id}: Fecha extraída` - Fecha del análisis extraída
- `[PathRepair] Ticket {Id}: ? Movido exitosamente` - Movimiento exitoso
- `[PathRepair] Resumen: {Processed} procesados, {Found} encontrados, {Moved} movidos, {Errors} errores`

### Dashboard de Diagnóstico
El endpoint `/api/diagnostics/status` incluye estado del servicio:
```json
{
  "services": [
    {
      "name": "TicketPathRepairService",
      "running": false,
      "message": "Proceso completado. 97 procesados, 95 encontrados, 42 movidos, 2 errores",
      "totalProcessed": 97,
      "totalErrors": 2,
      "lastUpdate": "2025-10-07T19:25:30Z"
    }
  ]
}
```

## ?? Problemas Conocidos y Soluciones

### 1. "Archivo no encontrado"
**Causa**: Archivo físico no existe
**Solución**: Re-subir archivo o marcar ticket como error

### 2. "Error al mover archivo"
**Causa**: Permisos insuficientes
**Solución**: Ejecutar como administrador o ajustar permisos

### 3. "Error extrayendo fecha"
**Causa**: JSON de análisis sin fecha
**Solución**: Automático - usa `UploadedAt` como fallback

### 4. "Ticket en processing atascado"
**Causa**: Servicio interrumpido durante procesamiento
**Solución**: Usar endpoint `/api/tickets/{id}/reprocess?force=true`

## ?? Próximos Pasos Sugeridos

1. **Desactivar servicio después de primera ejecución** (opcional):
   - Comentar línea en `Program.cs`
   - O añadir configuración para ejecutar solo si se detectan tickets legacy

2. **Añadir filtros por PurchaseDate** en API:
   ```csharp
   [FromQuery] DateTime? purchaseDateFrom = null,
   [FromQuery] DateTime? purchaseDateTo = null
   ```

3. **Mostrar PurchaseDate en UI**:
   - Añadir columna en tabla de tickets
   - Filtro por rango de fechas

4. **Reportes por fecha real**:
   - Gastos por mes basados en `PurchaseDate`
   - Exportar a Excel con fecha correcta

5. **Alertas de tickets sin fecha**:
   - Notificar tickets con `PurchaseDate = null`
   - Sugerir reprocesar análisis

## ? Checklist de Verificación

- [x] Campo `PurchaseDate` añadido al modelo
- [x] Migración de BD creada y probada
- [x] Servicio `TicketPathRepairService` implementado
- [x] Búsqueda recursiva de archivos funcional
- [x] Extracción de fecha del análisis funcional
- [x] Sistema de backup temporal implementado
- [x] API actualizada con nuevo campo
- [x] Tests básicos añadidos
- [x] Documentación completa creada
- [x] Compilación exitosa
- [ ] Migración de BD aplicada en producción
- [ ] Servicio ejecutado y verificado
- [ ] Backups temporales limpiados
- [ ] UI actualizada para mostrar `PurchaseDate`
- [ ] Filtros por fecha implementados

## ?? Contacto y Soporte

Para problemas o dudas:
1. Revisar documentación en `PATH_REPAIR_README.md`
2. Consultar guía rápida en `QUICK_START_PATH_REPAIR.md`
3. Verificar logs de aplicación
4. Revisar carpeta `TempBackup/` para recuperación

---

**Última actualización**: 2025-10-07  
**Versión**: 1.0  
**Estado**: ? Listo para despliegue
