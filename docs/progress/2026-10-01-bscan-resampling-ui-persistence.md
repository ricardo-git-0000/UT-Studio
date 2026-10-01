# Integración UI y preferencia de remuestreo B-Scan

## Objetivo y resultado

Se integraron en la ventana B-Scan los cinco modos existentes, sin algoritmos nuevos. `None` sigue como
predeterminado; Lanczos-3 se identifica como alta calidad con cadencia reducida. El ViewModel distingue
solicitado, pendiente y presentado, conserva el último raster confirmado y acepta únicamente candidatos
del modo vigente.

## Decisiones confirmadas

La preferencia es configuración UI versionada por usuario, no dato de proyecto ni adquisición. Se guarda
solo `schemaVersion` y `bScan.resamplingMode`; cualquier entrada ausente, corrupta o desconocida vuelve a
`None`. No existe fallback silencioso durante render.

## Archivos y validación

Se modificaron Presentation, composición/ventana App.Wpf, ADR 0014, plan y arquitectura; se añadieron el
servicio JSON y pruebas de persistencia/estado. La solución Release terminó sin warnings; Core pasó
432/432 y WPF 97/97. No se añadieron paquetes, no se prepararon archivos y no se hizo commit.

## Riesgos y pendientes

La inspección manual debe confirmar legibilidad, foco, teclado, resize y DPI 96/120/144/192. La baseline
en hardware físico y cualquier integración GP/Kriging permanecen fuera de alcance.

La inspección manual detectó que los modos interpolados bloqueaban el ratón. La causa era una falsa
frontera asíncrona: `BScanVisualDelivery.GetRegionAsync` ejecutaba `BuildRegion` síncronamente y el
ViewModel podía invocarlo inline desde el Dispatcher cuando la operación previa ya estaba completada.
La llamada al proveedor de raster se despacha ahora al scheduler de trabajo; la aplicación y el swap
del resultado continúan serializados por el Dispatcher. Una barrera determinista comprueba que una
petición Lanczos puede permanecer dentro del proveedor mientras el Dispatcher sigue procesando estado.

La inspección live posterior mostró bandas verticales negras y `Invalid B-Scan horizontal shift
descriptor`. Las bandas eran `NoData` real: el render interpolado retenía todos los leases del ring y
la adquisición había rechazado 843 columnas por slots fijados. La ruta interpolada materializa ahora
una malla fuente acotada, libera todos los leases antes del kernel y devuelve el owner en cualquier
salida. No se rellenan gaps ni se relaja la máscara. Además, un scroll cuya región dirty no coincide con
el desplazamiento se recupera como full, evitando publicar descriptores incoherentes.

Una run congelada después de Stop continúa siendo remuestreable sin nuevas adquisiciones. Para que el
raster completo 1024² pueda conservar simultáneamente el resultado `None`, el candidato y la copia
fuente dentro del presupuesto, esta última usa tres owners separados de intensidad, máscara/procedencia
compartida y segmentos. La regresión productiva cierra y drena el run, cambia a Bicubic y confirma la
nueva firma, el bitmap y los balances.

Al reducir `Tamaño B`, `None` mantiene una columna de raster por traza visible para conservar su camino
compatible. Los modos interpolados usan en cambio el ancho físico solicitado por la superficie. Antes,
ambas rejillas tenían el mismo ancho y la interpolación coincidía con `None`, quedando el escalado
nearest-neighbor de WPF como único aumento visible. Las regresiones cubren cambiar el modo después de
reducir, reducir conservando Bicubic y la supersession inmediata reducción→Bicubic.

La malla temporal tenía el mismo alto y coordenadas que el destino, por lo que el kernel era identidad
en Y y un zoom con pocas muestras repetía bandas horizontales. Ahora se forma una fila fuente por muestra
RF visible al ampliar y se interpola hasta la altura física; al reducir se mantiene una fila peak por
huella. Una referencia independiente verifica el vector bilinear vertical exacto de cuatro muestras a
ocho filas, incluida máscara y procedencia derivada.
