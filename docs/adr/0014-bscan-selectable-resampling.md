# ADR 0014 — Remuestreo B-Scan seleccionable y reconstrucción separada

Fecha: 2026-09-29. Estado: Propuesto; pendiente de aprobación antes de implementar.
Trazabilidad: R01, R06, R08, R09, R13 y R14. Complementa, no sustituye,
[ADR 0013](0013-bscan-bounded-live-visualization.md).

## Contexto y distinción normativa

El B-Scan actual forma, desde RF adquirido, un raster neutral `BipolarIntensity8` al tamaño solicitado.
Cada celda representa el extremo de mayor magnitud de la huella de muestras y trazas que le corresponde;
el valor cero está reservado a `NoData`. Después App.Wpf aplica la LUT y WPF confirma el bitmap. La
selección B→A se resuelve contra el rango residente y recupera una traza adquirida exacta.

Esta decisión distingue dos operaciones que no son intercambiables:

- **Remuestreo visual**: cambia la rejilla de presentación de una intensidad neutral. No modifica RF,
  store, identidades, coordenadas ni selección; sus resultados son `VisualDerived`.
- **Reconstrucción espacial**: estima valores no medidos o ausentes. Tiene significado NDT, exige
  coordenadas físicas y un modelo declarado, produce `SpatialEstimated` con incertidumbre y nunca se
  presenta como adquirido.

`None`, `Nearest`, `Bilinear`, `Bicubic` y `Lanczos` son modos visuales, siempre que operen dentro de
soporte adquirido válido y no cierren gaps. Gaussian Process/Kriging es reconstrucción; queda fuera del
primer incremento y requerirá otra decisión y aceptación NDT.

## Decisiones confirmadas por los requisitos de esta tarea

- RF, `BScanTraceStore`, `AcquisitionIndex`, `Sequence`, timestamps y coordenadas son inmutables para
  esta funcionalidad. La selección devuelve únicamente una traza adquirida.
- `None` es el valor predeterminado y conserva byte a byte el comportamiento actual, incluida la
  formación por huella de píxel, los full/incrementales y el escalado WPF nearest vigente.
- El remuestreo ocurre sobre intensidad neutral antes de LUT; BGRA nunca es dominio de interpolación.
- `NoData` y los segmentos discontinuos son autoridad separada del valor. Ningún kernel cruza una
  discontinuidad ni rellena un hueco silenciosamente.
- Run, revisión, wrap, viewport, resolución, modo, parámetros normalizados y versión del algoritmo
  forman la identidad de render. Un candidato con firma anterior se descarta.
- Latest-only, cadencia máxima vigente, generaciones, cancelación, recuperación full, ownership y
  presupuestos acotados del ADR 0013 se mantienen.
- Se elige composición de servicios y modelos neutrales; no una jerarquía pública B/C-Scan ni un
  control universal.

## Modelo neutral propuesto

Se introducirán inicialmente en una carpeta/namespace `Scan2D` de `Visualization.Core`, sin crear ni
renombrar proyectos:

- `Scan2DInterpolationMode`: `None`, `Nearest`, `Bilinear`, `Bicubic`, `Lanczos`.
- `Scan2DResamplingOptions`: valor inmutable y validado, con modo, parámetros normalizados y versión de
  algoritmo. No contendrá parámetros de Kriging.
- `ScanRaster2D`/vista equivalente: ancho, alto, stride, formato de intensidad, máscara de validez,
  procedencia y región, sin ownership implícito.
- `RasterSampleProvenance`: `NoData`, `AcquiredSupport`, `VisualDerived`, `SpatialEstimated`. En este
  incremento el remuestreador solo puede producir los tres primeros.
- `RasterRenderSignature`: transformaciones, dimensiones, modo, parámetros canónicos, revisión de
  configuración y versión del algoritmo.
- una interfaz pequeña `IScan2DResampler` o estrategia interna equivalente, pura y cancelable. El
  delivery conserva pools, owners, concurrencia y publicación.

`BScanRenderRequest`, snapshot e identidad mostrada incorporarán la configuración estructural; no se
confiará únicamente en un `ulong` opaco. La máscara inicial podrá ocupar un byte por celda para mantener
operaciones simples y acotadas; compactarla exige benchmark, no cambia su semántica.

### Validez, procedencia y full/incremental

La intensidad `0` actual seguirá siendo compatible como codificación externa de `NoData`, pero los
kernels consultarán la máscara autoritativa y nunca usarán cero como muestra numérica. La procedencia
del destino es `AcquiredSupport` si corresponde exactamente a una celda adquirida sin mezcla,
`VisualDerived` si combina o replica soporte adquirido y `NoData` si el soporte exigido no es válido.

Full e incremental ejecutan el mismo algoritmo y deben ser idénticos. Un incremental amplía su dirty
region por el halo del kernel; si falta soporte, cambia la firma, hay wrap o no puede probarse la base,
se cancela y se solicita una única recuperación full. El rango residente sigue siendo la autoridad para
hit testing y selección; la región expandida solo delimita trabajo de raster.

## Semántica de modos visuales

### Malla fuente y reducción

El raster actual coincide con la rejilla destino; por sí solo no ofrece una malla diferente que
Bilinear/Bicubic/Lanczos puedan remuestrear. La implementación deberá separar explícitamente:

1. dominio y extensión B autoritativos;
2. malla fuente neutral formada desde RF, con dimensiones, stride, paso, máscara y segmentos;
3. malla destino del viewport y resolución física solicitados;
4. reducción antialias determinista al disminuir resolución y kernel seleccionado al aumentarla.

No se permite aplicar un segundo filtro arbitrario a una imagen ya formada al mismo tamaño. `None`
conserva la reducción actual directa. Para los demás modos, la primera propuesta a validar es formar
una malla fuente con una columna por traza residente y una resolución temporal acotada derivada del
índice min/max existente, y después remuestrear al destino. La resolución temporal, su relación con el
bucket de 64 muestras y el presupuesto son decisiones de Fase 1 que requieren benchmark/aprobación;
la firma siempre incluirá ambas geometrías. Reducir resolución continúa usando la política conservadora
de extremo RF por huella para no perder picos; los kernels de este ADR no sustituyen esa reducción.

La transformación entre centros es, por eje,
`s = (d + 0.5) * sourceLength / destinationLength - 0.5`. Los índices geométricos se limitan al primer
y último centro del mismo segmento. Una dimensión fuente de longitud uno usa ese único soporte con
peso uno. La acumulación usa orden fijo, precisión definida por implementación y redondeo documentado.

| Modo | Soporte/kernel | Bordes, `NoData` y gaps | Coste por píxel | Riesgos |
| --- | --- | --- | ---: | --- |
| None | Camino actual; sin rejilla intermedia obligatoria | Conserva exactamente el sentinel y discontinuidades actuales | O(1) más formación | Es la referencia de compatibilidad |
| Nearest | Centro más próximo; empate hacia el índice menor | Válido solo si la celda elegida es válida y del segmento | 1 tap | Bloques/aliasing; no inventa valores |
| Bilinear | Separable 2×2, radio 1 | Los cuatro taps requeridos deben ser válidos y del mismo segmento; si no, `NoData` | 4 taps | Suavizado, sin overshoot |
| Bicubic | Keys separable 4×4, radio 2 | Política conservadora idéntica; bordes repiten solo dentro del segmento | 16 taps | Overshoot; clamp final a intensidad [1,255] |
| Lanczos | `sinc(x)sinc(x/a)`, separable 6×6 con `a=3` propuesto | Todos los taps reales requeridos válidos; normalización determinista; no cruza gaps | 36 taps | Ringing y overshoot; clamp final [1,255] |
| GP/Kriging | Vecindario espacial y sistema de covarianza | Puede estimar huecos, por lo que no es modo visual | al menos O(P·k²), O(P·k³) sin factorización reutilizable | Coste, modelo y falsa certeza NDT |

No se renormalizan subconjuntos arbitrarios de taps junto a `NoData`: hacerlo extendería datos dentro de
huecos. En bordes geométricos sí se combinan taps repetidos del mismo segmento y se normalizan los pesos.
Bicubic propone Keys/Catmull–Rom `a=-0.5`; Lanczos propone `a=3`. Ambos parámetros requieren aprobación.

## Gaussian Process / Kriging

Kriging solo será evaluable cuando haya coordenadas físicas calibradas, unidades y semántica espacial
confirmadas; `AcquisitionIndex` no se aceptará por defecto como distancia. Una fase independiente deberá
definir, como mínimo:

- covarianza o variograma (propuesta inicial a evaluar: Matérn anisotrópico), nugget, sill, longitudes
  por eje y unidades físicas;
- vecindario máximo acotado (hipótesis de benchmark: `k <= 32`) y búsqueda espacial acotada;
- cache con clave de geometría/modelo, límite en bytes, cancelación y descarte latest-only;
- media estimada y varianza/desviación estándar por celda;
- máscara `SpatialEstimated`, leyenda y patrón/overlay que no dependa solo del color;
- prohibición de usar valores estimados como traza seleccionable o medida adquirida.

El coste ingenuo de factorizar por píxel es O(P·k³); aun reutilizando factorizaciones, la predicción es
O(P·k²) y añade matrices/caches. Sin benchmarks, presupuesto y revisión NDT no cumple el live acotado y
queda aplazado.

## Pipeline y responsabilidades

```text
RF/store B autoritativo
  → formación/reducción B específica
  → intensidad neutral + validez/procedencia
  → remuestreo Scan2D neutral (excepto None, camino compatible)
  → LUT en App.Wpf
  → staging y commit transaccional de bitmap en Visualization.Wpf
```

| Proyecto | Responsabilidad |
| --- | --- |
| `Domain` / `Contracts` / `Application` | Sin cambios: identidades, adquisición y sesión no conocen el modo visual |
| `Visualization.Core` | modelos Scan2D neutrales, validación, kernels, máscaras, firma, métricas; `BScanVisualDelivery` forma B, compone el remuestreador y posee candidatos |
| `Presentation` | `BScanViewModel` posee opción seleccionada, errores y transición atómica; cancela la generación anterior y solicita una full |
| `App.Wpf` | selector/editores, binding y adaptación; LUT después del remuestreo |
| `Visualization.Wpf` | superficie, DPI, staging, rollback y commit; mantiene `BitmapScalingMode.NearestNeighbor` y no conoce kernels |

Cambiar modo o parámetros, zoom, resize o run publica primero un snapshot coherente de viewport,
resolución y opciones; después notifica. Cancela el candidato anterior y causa exactamente una full. Un
candidato solo confirma si coinciden firma, generación, run, revisión, wrap y token. Stop y cierre tienen
prioridad y esperan owners/operaciones con las barreras existentes.

## Experiencia de usuario

La ventana ofrece un `ComboBox` etiquetado: None, Nearest, Bilinear, Bicubic y Lanczos. El modo activo
permanece visible junto al raster y accesible mediante nombre, ayuda, estado y teclado; no reutiliza los
atajos de navegación actuales. Editores específicos aparecen solo cuando proceda, con rango, unidad,
validación en línea y conservación del último valor válido. El cambio funciona en live y sobre run
sellada; durante cálculo se muestra «Aplicando…» sin bloquear Stop o cierre.

Kriging aparece, como máximo, separado bajo «Reconstrucción avanzada (estimada)» y no seleccionable
hasta aprobar su fase. Adquirido, derivado visual y estimado no dependerán únicamente del color para ser
distinguibles. La preferencia no se persiste en las Fases 1–3; la Fase 4 decidirá si es solo de sesión o
configuración UI versionada. Un valor desconocido siempre vuelve a `None` y nunca altera datos.

## Presupuesto inicial y degradación

Son objetivos diagnósticos, no garantías ni baseline aprobada:

| Resolución full | None/Nearest | Bilinear | Bicubic | Lanczos-3 |
| --- | ---: | ---: | ---: | ---: |
| 256×256 | ≤2 ms | ≤4 ms | ≤8 ms | ≤12 ms |
| 1024×1024 | ≤12 ms | ≤20 ms | ≤45 ms | ≤80 ms |

Para incrementales de 1/8/33/100 columnas se medirán tiempo y asignaciones incluyendo halos 0/1/2/3.
No se inferirá proporcionalidad sin medir. A 1024², intensidad fuente+destino ocupa 2 MiB, máscaras
byte fuente+destino 2 MiB, procedencia compacta hasta 1 MiB y scratch reutilizable objetivo ≤1 MiB:
pico neutral adicional objetivo ≤6 MiB (≤0,5 MiB a 256²), que debe probarse dentro de los 16 MiB
visuales existentes contando owners coexistentes. El pico WPF actual de aproximadamente 12 MiB por
control se mantiene separado.

Si un modo no alcanza 30 Hz, se coalescen/cancelan candidatos, se conserva el último raster confirmado
y baja la cadencia efectiva. Nunca se cambia de kernel, se rellena `NoData` o se publica una calidad
inferior silenciosamente. Se diagnostican duración, píxeles/taps, cancelados, sustituidos, full por
causa, bytes activos/pico y modo/firma.

## Reutilización B-Scan/C-Scan

### Común justificable ahora

Raster 2D, dimensiones/stride/regiones, validez, procedencia, paleta/normalización neutral, opciones y
kernels, transformaciones centro-a-centro, firma, halos, cancelación, buffers y métricas son comunes.
Los helpers internos WPF de staging/commit pueden extraerse cuando la implementación demuestre una
segunda utilización.

### Específico

Formación desde trazas, `AcquisitionIndex`, selección B→A, sincronización A/B, eje temporal, auto-follow,
wrap y discontinuidad de frames siguen en B-Scan. Agregación por posición, gates, encoder/trayectoria y
semántica de celda pertenecerán al futuro C-Scan.

### Composición y migración

No existe una relación «es-un» estable entre B y C. Se eligen modelos neutrales e interfaces pequeñas
compuestas por deliveries específicos. No se crea todavía `Scan2D.Core`, un `Scan2DViewport` universal,
una base pública WPF, un control universal, identidad genérica de scan ni tipos ficticios C-Scan. Esas
extracciones se reconsideran con requisitos C concretos.

La migración conserva las API `BScan*`: primero se añaden tipos neutrales; después `None` demuestra
equivalencia; Nearest entra full-first; Bilinear/Bicubic y Lanczos incorporan full antes de incremental;
por último UI y persistencia. Pruebas de arquitectura prohibirán WPF en Core/Presentation, referencias
de interpoladores a tipos `BScan*`, y dependencia de Visualization.Wpf distinta de Visualization.Core.

En la Fase 1 implementada, `Scan2DSourceGrid` es una vista no propietaria con centros expresados como
`FirstCenter + index * CenterStep`, stride independiente y buffers separados de intensidad, máscara,
segmento y procedencia. El resultado usa un único owner acotado con tres slices contiguos byte por celda
(intensidad, máscara y procedencia). Nearest forma y remuestrea bloques de hasta 64 filas con un owner
scratch separado de intensidad, máscara, segmento compacto y procedencia; se contabiliza contra el
presupuesto global y se libera antes de transferir el resultado. La máscara se transporta
hasta la LUT WPF y es la única autoridad de NoData. `NearestNeighbor` es full-only en esta fase: una
petición con base previa fuerza una full por cambio de configuración, porque el dirty B actual describe
la huella de `None` y todavía no prueba el soporte inverso de Nearest.

## Alternativas descartadas

- interpolar BGRA o usar el escalado WPF como implementación: mezcla la LUT y `NoData`;
- tratar byte cero como intensidad: convierte ausencia en RF válido;
- renormalizar sobre taps ausentes o cruzar segmentos: relleno silencioso;
- incluir Kriging como filtro live: confunde estimación y visualización;
- clase base pública/control universal B/C: semánticas aún diferentes y generalización prematura;
- proyecto nuevo o genéricos públicos por píxel: coste sin segundo consumidor demostrado;
- degradar automáticamente a otro algoritmo: el indicador de modo dejaría de ser veraz.

## Consecuencias y riesgos NDT

El diseño añade memoria, CPU, firma y pruebas, pero mantiene intactos los datos adquiridos. Bilinear,
bicubic y Lanczos pueden suavizar picos; bicubic/Lanczos pueden producir overshoot/ringing aun con clamp.
Por ello son presentación, no evidencia nueva, y el modo debe ser visible. La política conservadora de
máscara puede ampliar visualmente un gap por el radio del kernel, preferible a ocultarlo. Cualquier
futura medida sobre raster deberá informar procedencia; las medidas exactas continúan resolviéndose
contra datos adquiridos.

## Parámetros y decisiones aplazadas

Requieren aprobación: significado visible de None frente a Nearest; Keys `a=-0.5`; Lanczos `a=3`;
política conservadora de todos los taps válidos; ubicación inicial en Visualization.Core sin proyecto
nuevo; persistencia de preferencia; objetivos de memoria/tiempo y hardware; y exclusión de Kriging del
incremento. Variograma, parámetros, unidades y UX de incertidumbre de Kriging quedan para su ADR.
