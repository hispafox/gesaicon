# ?? Corrección Automática de Tickets Legacy

## ¿Qué hace?

Al iniciar la aplicación, automáticamente:

1. ? **Detecta tickets legacy** sin estructura correcta
2. ? **Lee la empresa de la BD** (campo `CompanyName`) y la convierte a slug
3. ? **Extrae la fecha real del ticket** desde `AnalysisJson`
4. ? **Mueve archivos** a la nueva estructura `empresas/{slug}/{yyyy}/{MM}/`
5. ? **Actualiza la base de datos** con los campos correctos
6. ? **Migra archivos de análisis** markdown asociados

## Modos de Operación

### ?? Modo Normal (por defecto)

**`ForceReprocessAll: false`**

Solo procesa tickets que necesitan corrección:
- Sin `RelativePath` (estructura legacy antigua)
- Con `CompanySlug = null` o `= "default"`

Este es el modo normal de operación.

### ?? Modo Forzado (reprocesamiento completo)

**`ForceReprocessAll: true`**

Reprocesa **TODOS los tickets** sin excepción:
- Útil cuando hubo un error en la migración anterior
- Recalcula empresa y fecha para todos
- Mueve archivos aunque ya estén en una ubicación

**?? IMPORTANTE:** Recuerda volver a poner `false` después de corregir el problema.

## Configuración

En `appsettings.json`:

```json
"FileIngestion": {
  "DefaultCompanySlug": "miempresa",   // <-- Empresa por defecto
  "ForceReprocessAll": false           // <-- Cambiar a true para reprocesar todo
}
```

### ¿Cuándo usar `ForceReprocessAll: true`?

? Cuando todos los tickets se fueron a la carpeta incorrecta  
? Cuando necesitas cambiar la lógica de extracción de fecha  
? Cuando hubo un error en la conversión de empresa a slug  
? Después de corregir un bug en el código de migración  

**Pasos:**
1. Edita `appsettings.json` y pon `"ForceReprocessAll": true`
2. Reinicia la aplicación (F5)
3. Revisa los logs para confirmar que todo se procesó correctamente
4. **IMPORTANTE:** Vuelve a poner `"ForceReprocessAll": false`

## Conversión de Empresa a Slug

El servicio **respeta la empresa que ya está en la base de datos** en el campo `CompanyName`:

| CompanyName (BD) | CompanySlug generado | Ruta física |
|------------------|---------------------|-------------|
| `McDonald's` | `mcdonalds` | `empresas/mcdonalds/2025/03/` |
| `E.Leclerc` | `eleclerc` | `empresas/eleclerc/2025/03/` |
| `Lidl Supermercados S.A.U` | `lidl-supermercados-sau` | `empresas/lidl-supermercados-sau/2025/03/` |
| `Ahorramas` | `ahorramas` | `empresas/ahorramas/2025/03/` |
| `IKEA` | `ikea` | `empresas/ikea/2025/03/` |
| `Carrefour` | `carrefour` | `empresas/carrefour/2025/03/` |
| `Centros Comerciales Carrefour S.A` | `centros-comerciales-carrefour-sa` | `empresas/centros-comerciales-carrefour-sa/2025/03/` |
| *(null o vacío)* | `default` | `empresas/default/2025/03/` |

## Extracción de Fecha

El servicio intenta extraer la **fecha real del ticket** en este orden:

1. **Desde `AnalysisJson`** (campos: `date`, `ticketDate`, `fecha`, `Date`, `transactionDate`)
2. **Desde `ExpenseYear` / `ExpenseMonth`** en la BD
3. **Fallback: `UploadedAt`** (fecha de carga)

## ¿Cuándo se ejecuta?

- **Al arrancar la API** (una sola vez por arranque)
- **No interfiere** con el funcionamiento normal
- **Solo en ambiente Development/Production** (se salta en Testing)

## Logs

### Modo Normal:
```
[LegacyFix] Iniciando corrección de 10 tickets (ForceReprocessAll=False)
[LegacyFix] Ticket 97 (McDonald's) ? empresas/mcdonalds/2025/03/
```

### Modo Forzado:
```
[LegacyFix] MODO FORZADO ACTIVADO: reprocesando TODOS los tickets
[LegacyFix] Iniciando corrección de 97 tickets (ForceReprocessAll=True)
[LegacyFix] Ticket 97 (McDonald's) ? empresas/mcdonalds/2025/03/
...
[LegacyFix] Corrección completada: 97 corregidos, 0 omitidos, 0 errores
```

## Ejemplo Real

**Antes del arranque:**
```
Ticket 94:
- FileName: "eeeb5371-ab9b-46d2-b0b1-754fb115b2bd.jpg"
- CompanyName: "Lidl Supermercados S.A.U"
- CompanySlug: "default" (incorrecto)
- RelativePath: "empresas/default/2025/10/..." (fecha incorrecta)
```

**Después del arranque (con ForceReprocessAll=true):**
```
Ticket 94:
- FileName: "eeeb5371-ab9b-46d2-b0b1-754fb115b2bd.jpg"
- CompanyName: "Lidl Supermercados S.A.U" (sin cambios)
- CompanySlug: "lidl-supermercados-sau" (corregido)
- RelativePath: "empresas/lidl-supermercados-sau/2025/03/..." (fecha del ticket)
- FileUrl: "/Uploads/empresas/lidl-supermercados-sau/2025/03/..."
```

## ¿Es seguro?

? **Sí**, porque:
- **Lee la empresa de la BD**, no la cambia
- Verifica que el archivo exista antes de moverlo
- Protege contra duplicados
- Solo actualiza BD si el movimiento físico fue exitoso
- Migra también archivos de análisis asociados
- Captura errores por ticket sin detener el resto
- **En modo forzado**, recalcula todo desde cero

## Desactivar corrección automática

Si NO quieres que se ejecute automáticamente, simplemente comenta la línea en `Program.cs`:

```csharp
// builder.Services.AddHostedService<LegacyTicketFixService>();
```

## Resumen

?? **Modo Normal**: Solo corrige tickets con problemas  
? **Modo Forzado**: Reprocesa absolutamente todo  
?? **El servicio respeta la empresa que ya está en la BD**  
?? **Extrae la fecha real del ticket desde el análisis JSON**  
?? **Se ejecuta automáticamente al arrancar**  
? **No requiere configuración manual por empresa**  

¡Cada ticket se organiza según su propia empresa y fecha real! ??
