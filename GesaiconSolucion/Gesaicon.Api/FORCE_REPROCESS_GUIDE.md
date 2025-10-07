# ?? Guía Rápida: Reprocesar Todos los Tickets

## Problema: "¡Todos mis tickets se fueron a la carpeta incorrecta!"

Si ejecutaste la migración y algo salió mal (por ejemplo, todos se fueron a `default/` o con fechas incorrectas), puedes **reprocesar todo** fácilmente.

## Solución en 3 Pasos

### 1?? Edita `appsettings.json`

Abre el archivo y cambia:

```json
"FileIngestion": {
  "DefaultCompanySlug": "default",
  "ForceReprocessAll": true,    // <-- CAMBIA ESTO A true
  ...
}
```

### 2?? Reinicia la Aplicación

Presiona **F5** en Visual Studio (o detén y vuelve a iniciar).

La aplicación procesará **TODOS** los tickets automáticamente al arrancar.

### 3?? Verifica y Revierte

En los logs verás:

```
[LegacyFix] MODO FORZADO ACTIVADO: reprocesando TODOS los tickets
[LegacyFix] Iniciando corrección de 97 tickets (ForceReprocessAll=True)
[LegacyFix] Ticket 1 (McDonald's) ? empresas/mcdonalds/2025/03/
[LegacyFix] Ticket 2 (IKEA) ? empresas/ikea/2025/03/
...
[LegacyFix] Corrección completada: 97 corregidos, 0 omitidos, 0 errores
```

**IMPORTANTE:** Después de verificar que todo está correcto, vuelve a poner:

```json
"ForceReprocessAll": false,    // <-- VOLVER A false
```

Y reinicia de nuevo para que no vuelva a reprocesar en cada arranque.

## ¿Qué hace el Modo Forzado?

? Reprocesa **TODOS** los tickets (incluso los que ya están correctos)  
? Extrae la **fecha real** desde el análisis JSON del ticket  
? Usa el **CompanyName** de la BD para generar el slug correcto  
? Mueve archivos físicamente a su ubicación correcta  
? Actualiza la base de datos  

## Casos de Uso

| Situación | Solución |
|-----------|----------|
| Todos se fueron a `default/` | `ForceReprocessAll: true` |
| Fechas incorrectas (año/mes) | `ForceReprocessAll: true` |
| Empresas mal convertidas a slug | `ForceReprocessAll: true` |
| Cambio en lógica de extracción | `ForceReprocessAll: true` |
| Solo faltan algunos tickets | `ForceReprocessAll: false` (modo normal) |

## Estructura Final

Después del reprocesamiento, tus archivos quedarán así:

```
Uploads/empresas/
??? mcdonalds/
?   ??? 2025/
?       ??? 03/
?           ??? ticket1.jpg
?           ??? ticket2.jpg
??? lidl-supermercados-sau/
?   ??? 2025/
?       ??? 03/
?           ??? ticket3.jpg
??? carrefour/
?   ??? 2025/
?       ??? 03/
?       ??? 04/
??? default/                 <-- Esta carpeta quedará vacía (puedes borrarla)
```

## Seguridad

- ? No pierde datos
- ? Protege contra duplicados
- ? Captura errores por ticket
- ? Solo actualiza BD si el movimiento físico fue exitoso

## Recordatorio

?? **NO OLVIDES volver a poner `ForceReprocessAll: false`** después de corregir el problema.

Si lo dejas en `true`, reprocesará todo en cada arranque (innecesariamente).

## Resumen

```bash
1. ForceReprocessAll: true  ? Reiniciar
2. Verificar logs
3. ForceReprocessAll: false ? Reiniciar
```

¡Listo! ??
