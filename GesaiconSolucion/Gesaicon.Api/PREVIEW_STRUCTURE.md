# Previsualización de Conversión Empresa ? Slug

Basándome en tu captura de pantalla, así quedarían organizados tus tickets:

## Estructura de Carpetas Generada

**IMPORTANTE:** Todos tus tickets son de **Octubre 2025** (subidos el 2025-10-06), por lo que quedarán en la carpeta `2025/10/`

```
Uploads/empresas/
??? mcdonalds/
?   ??? 2025/
?       ??? 10/
?           ??? 60f9f191-8440-4845-9616-65c7d5794ba9.jpg (Ticket 97)
?           ??? a334ed9b-6b19-441c-9503-602baafba2e9.jpg (Ticket 96)
?
??? eleclerc/
?   ??? 2025/
?       ??? 10/
?           ??? 3f59f1ff-f65d-4b79-9243-7471fe435798.jpg (Ticket 95)
?
??? lidl-supermercados-sau/
?   ??? 2025/
?       ??? 10/
?           ??? eeeb5371-ab9b-46d2-b0b1-754fb115b2bd.jpg (Ticket 94)
?
??? ahorramas/
?   ??? 2025/
?       ??? 10/
?           ??? 71c7ca55-2b78-48f2-aec5-5b0f7068dd4c.jpg (Ticket 93)
?           ??? 981e0d4a-e271-4a7b-acd5-a77e599a6914.jpg (Ticket 92)
?           ??? 196286a4-ddee-4507-aa8f-5d19ea722cfd.jpg (Ticket 90)
?           ??? 0b1733f6-da06-4b73-92a6-800e783ec8d6.jpg (Ticket 88)
?           ??? d4703a32-024d-4a09-a170-41e94245fdc4.jpg (Ticket 86)
?           ??? 021ec3e0-2898-445b-9c7c-32136827497d.jpg (Ticket 83)
?
??? ikea/
?   ??? 2025/
?       ??? 10/
?           ??? acf49b8a-5c75-4241-bb5a-c9881f64400.jpg (Ticket 91)
?           ??? 21732121-64a3-43c7-91ed-50ce099fd625.jpg (Ticket 89)
?
??? carrefour/
?   ??? 2025/
?       ??? 10/
?           ??? af4d6328-f300-432a-a71f-5da8f028a80a.jpg (Ticket 87)
?           ??? b0d28425-f8b5-4693-8680-837531df41b5.jpg (Ticket 85)
?
??? centros-comerciales-carrefour-sa/
?   ??? 2025/
?       ??? 10/
?           ??? 3059a7a0-4b6c-4d4f-ae0b-2b7048be14ae.jpg (Ticket 84)
?
??? laura-pla/
    ??? 2025/
        ??? 10/
            ??? 3b2cf034f-efd5-4626-b1eb-92ccf1f27e71.jpg (Ticket 82)
```

## Conversión de Nombres

| Ticket | CompanyName (BD) | CompanySlug (generado) | Ruta Completa |
|--------|------------------|------------------------|---------------|
| 97 | McDonald's | `mcdonalds` | `empresas/mcdonalds/2025/10/` |
| 96 | McDonald's | `mcdonalds` | `empresas/mcdonalds/2025/10/` |
| 95 | E.Leclerc | `eleclerc` | `empresas/eleclerc/2025/10/` |
| 94 | Lidl Supermercados S.A.U | `lidl-supermercados-sau` | `empresas/lidl-supermercados-sau/2025/10/` |
| 93 | Ahorramas | `ahorramas` | `empresas/ahorramas/2025/10/` |
| 92 | Ahorramas | `ahorramas` | `empresas/ahorramas/2025/10/` |
| 91 | IKEA | `ikea` | `empresas/ikea/2025/10/` |
| 90 | Ahorramas | `ahorramas` | `empresas/ahorramas/2025/10/` |
| 89 | IKEA | `ikea` | `empresas/ikea/2025/10/` |
| 88 | Ahorramas | `ahorramas` | `empresas/ahorramas/2025/10/` |
| 87 | Carrefour | `carrefour` | `empresas/carrefour/2025/10/` |
| 86 | Ahorramas | `ahorramas` | `empresas/ahorramas/2025/10/` |
| 85 | Carrefour | `carrefour` | `empresas/carrefour/2025/10/` |
| 84 | Centros Comerciales Carrefour S.A | `centros-comerciales-carrefour-sa` | `empresas/centros-comerciales-carrefour-sa/2025/10/` |
| 83 | Ahorramas | `ahorramas` | `empresas/ahorramas/2025/10/` |
| 82 | LAURA PLA | `laura-pla` | `empresas/laura-pla/2025/10/` |

## Lógica de Fecha

El servicio usa esta lógica para determinar año y mes:

```csharp
var year = ticket.ExpenseYear ?? ticket.UploadedAt.Year;   // 2025
var month = ticket.ExpenseMonth ?? ticket.UploadedAt.Month; // 10 (Octubre)
```

Si tus tickets tienen `ExpenseYear` o `ExpenseMonth` en la BD, usa esos valores.
Si no, usa la fecha de `UploadedAt` (que según tu captura es **2025-10-06 23:10**).

## Resultado

- ? Cada empresa en su propia carpeta
- ? Organizados por año/mes: `2025/10/`
- ? Nombres válidos para sistema de archivos
- ? Fácil de buscar y organizar
- ? Compatible con Windows, Linux y Azure Blob Storage

## Logs Esperados

Al ejecutar verás:

```
[LegacyFix] Iniciando corrección automática de 16 tickets
[LegacyFix] Ticket 97 (McDonald's) ? empresas/mcdonalds/2025/10/
[LegacyFix] Ticket 96 (McDonald's) ? empresas/mcdonalds/2025/10/
[LegacyFix] Ticket 95 (E.Leclerc) ? empresas/eleclerc/2025/10/
[LegacyFix] Ticket 94 (Lidl Supermercados S.A.U) ? empresas/lidl-supermercados-sau/2025/10/
...
[LegacyFix] Corrección completada: 16 corregidos, 0 omitidos, 0 errores
```

## Ejecutar

Simplemente **arranca la aplicación desde Visual Studio** (F5) y revisa los logs.

Los archivos se moverán automáticamente a su ubicación correcta según su empresa y fecha.
