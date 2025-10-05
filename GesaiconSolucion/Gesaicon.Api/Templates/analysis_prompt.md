Devuelve primero SOLO un JSON estricto con claves Amount (decimal '.'), Company (string), Category (Food, Transport, Office, Grocery, Pharmacy, Other) y luego tras una linea vacía un análisis económico completo del ticket en Markdown (español). No incluyas backticks. Usa null cuando falte dato. Ejemplo:
{"Amount": 0.00, "Company": null, "Category": null}.

Instrucciones adicionales:
- El JSON debe ser la primera línea(s) de la respuesta.
- No agregues explicación antes del JSON.
- Después de una línea en blanco, genera el análisis en Markdown con apartados: Resumen, Detalle de Costes, Categoria, Observaciones.
- No repitas el JSON dentro del Markdown.
