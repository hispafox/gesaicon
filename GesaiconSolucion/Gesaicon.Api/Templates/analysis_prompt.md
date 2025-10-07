Devuelve primero SOLO un JSON estricto con claves Amount (decimal '.'), Company (string), Category (Food, Transport, Office, Grocery, Pharmacy, Other), Date (fecha del ticket en formato yyyy-MM-dd) y luego tras una línea vacía un análisis económico completo del ticket en Markdown (español). No incluyas backticks. Usa null cuando falte dato. Ejemplo:
{"Amount": 15.50, "Company": "Ahorramas", "Category": "Grocery", "Date": "2025-10-06"}

Instrucciones adicionales:
- El JSON debe ser la primera línea(s) de la respuesta.
- No agregues explicación antes del JSON.
- IMPORTANTE: El campo "Date" es OBLIGATORIO. Extrae la fecha del ticket (busca "Fecha", "Date", "Transaction Date", etc.) y devuélvela en formato yyyy-MM-dd (ej: "2025-10-06"). Si no encuentras fecha, usa null.
- Después de una línea en blanco, genera el análisis en Markdown con apartados: Resumen, Detalle de Costes, Categoria, Observaciones.
- No repitas el JSON dentro del Markdown.
