# ADR 0010 — Medidas manuales aproximadas con cursores A-Scan

Fecha: 2026-09-18. Estado: Aceptado e implementado para el primer incremento.
Trazabilidad: R01, R02, R08 y R13; complementa [ADR 0008](0008-latest-only-visual-delivery.md) y [ADR 0009](0009-reusable-scan-visualization-controls.md).

## Contexto

El snapshot A-Scan visual puede contener una envolvente min/max reducida a un máximo de 1.024 puntos. No conserva necesariamente la muestra original correspondiente a cada posición temporal. Los cursores manuales necesitan sobrevivir a nuevos snapshots, ser independientes de WPF y no retener buffers de adquisición.

## Decisión

`UTStudio.Visualization.Core` define estado y cálculos neutrales e inmutables. Cada cursor conserva su posición temporal solicitada y una medida asociada al punto visual proyectado más próximo: índice, tiempo del punto y amplitud RF porcentual. La posición se limita al rango físico del snapshot. `Δt` es B menos A entre posiciones de cursor y `ΔRF` es la amplitud aproximada de B menos la de A.

La medida es explícitamente **aproximada sobre puntos visuales reducidos**. No representa precisión de muestra original ni habilita medidas certificadas. Una futura medición sobre muestras independientes requerirá otro contrato y un presupuesto de memoria propio.

`UTStudio.Presentation` es propietario del estado de cursores. Al llegar un snapshot nuevo conserva las posiciones temporales, las limita solamente si cambia el rango y vuelve a asociar la amplitud al punto visual más próximo. El estado incluye run y versión del snapshot; el renderizador solo lo presenta junto a la curva de esa misma identidad, incluso si la admisión visual retrasa el siguiente frame. Mostrar, ocultar, activar y restablecer producen nuevas instancias de estado.

`UTStudio.Visualization.Wpf` dibuja ejes, ticks, etiquetas y dos líneas de cursor mediante `DrawingContext`. Convierte el puntero a tiempo dentro del área de trazado, realiza hit-testing en unidades independientes de dispositivo y emite intenciones de activar o mover. No calcula amplitudes UT ni referencia Presentation. `UTStudio.App.Wpf` adapta esos eventos al ViewModel.

## Consecuencias

- El rango temporal procede de frecuencia de muestreo, cantidad de muestras y offset incluidos en el snapshot.
- La escala vertical continúa siendo RF firmada fija de −100 % a +100 %.
- El arrastre usa captura de ratón, se limita al trazado y termina también al perder captura.
- El redimensionamiento cambia solo la transformación gráfica; no cambia posiciones temporales.
- No se introducen profundidad, velocidad de material, gates, zoom, buffers crudos ni elementos WPF por tick o punto.

## Pendientes

- Medición sobre muestras originales con ownership y presupuesto explícitos, si se exige mayor precisión.
- Cursores horizontales, gates, zoom, profundidad y calibraciones de material.
- Validación visual prolongada en monitores con escalas DPI distintas.
