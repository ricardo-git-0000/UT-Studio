# ADR 0014 — Remuestreo B-Scan seleccionable y reconstrucción separada

Fecha: 2026-09-29. Estado: Aceptado; Fases 1–4 implementadas.
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

La geometría temporal fuente también es independiente del raster destino. Cuando el viewport contiene
menos muestras RF que filas físicas, se forma una fila fuente por celda de muestra y se remuestrea a la
altura del control; no se repite primero cada muestra en varias filas. Cuando contiene más muestras que
filas, la fuente conserva una fila por huella destino con selección de extremo para no perder picos y el
paso vertical coincide deliberadamente. Los centros se expresan en coordenadas de muestra: `i+0.5` para
la fuente nativa y centros uniformes dentro del viewport para el destino.

La transformación entre centros es, por eje,
`s = (d + 0.5) * sourceLength / destinationLength - 0.5`. Los índices geométricos se limitan al primer
y último centro del mismo segmento. Una dimensión fuente de longitud uno usa ese único soporte con
peso uno. La acumulación usa orden fijo, precisión definida por implementación y redondeo documentado.

| Modo | Soporte/kernel | Bordes, `NoData` y gaps | Coste por píxel | Riesgos |
| --- | --- | --- | ---: | --- |
| None | Camino actual; sin rejilla intermedia obligatoria | Conserva exactamente el sentinel y discontinuidades actuales | O(1) más formación | Es la referencia de compatibilidad |
| Nearest | Centro más próximo; empate hacia el índice menor | Válido solo si la celda elegida es válida y del segmento | 1 tap | Bloques/aliasing; no inventa valores |
| Bilinear | Separable 2×2, radio 1 | Los cuatro taps requeridos deben ser válidos y del mismo segmento; si no, `NoData` | 4 taps | Suavizado, sin overshoot |
| Bicubic | Keys separable 4×4, radio 2 | Los 16 taps deben ser válidos y del mismo segmento; bordes repiten solo dentro del segmento | 16 taps | Overshoot; clamp final a intensidad [0,255] |
| Lanczos | `sinc(x)sinc(x/3)`, separable 6×6 con `a=3` fijo | Los 36 taps requeridos son válidos y del mismo segmento; normalización completa determinista; no cruza gaps | 36 taps | Ringing y overshoot; clamp final [0,255] |
| GP/Kriging | Vecindario espacial y sistema de covarianza | Puede estimar huecos, por lo que no es modo visual | al menos O(P·k²), O(P·k³) sin factorización reutilizable | Coste, modelo y falsa certeza NDT |

No se renormalizan subconjuntos arbitrarios de taps junto a `NoData`: hacerlo extendería datos dentro de
huecos. En bordes geométricos sí se combinan taps repetidos del mismo segmento y se normalizan los pesos.
Bicubic fija Keys/Catmull–Rom `a=-0.5`; Lanczos fija el radio aprobado `a=3`.

La Fase 2 acumula Bilinear y Bicubic en `double`, en orden estable de filas y taps de izquierda a
derecha, y redondea el valor final con `MidpointRounding.AwayFromZero`. El clamp geométrico se aplica a
cada índice antes de validar máscara y segmento; el clamp de amplitud bicúbico se aplica después de la
suma completa. El rango es `[0,255]` porque intensidad cero válida se distingue de NoData mediante la
máscara. Los 4/16 taps se validan incluso si su peso matemático es cero. Ambos modos son full-only en
esta fase; los bloques B incorporan halo vertical 1/2 y no convierten sus fronteras internas en bordes.
En 1024×1024, resultado vigente y candidato consumen 6 MiB lógicos; el bloque máximo añade
aproximadamente 193 KiB lógicos. Con los buckets observados de `MemoryPool`, la coexistencia máxima
rentada es 8.650.752 bytes. El techo por consumidor contempla sus dos owners permitidos y el presupuesto
global de 16 MiB continúa siendo la autoridad; el scratch temporal se carga al global y se libera antes
de transferir el candidato.

La Fase 3 implementa Lanczos-3 full-only con offsets `-2..+3`, halo vertical 3 en los bloques B y halo
horizontal 3 limitado al rango residente autoritativo. Así el clamp geométrico replica únicamente un
borde residente real; un gap o cambio de segmento fuera del viewport pero dentro del soporte sigue
invalidando el destino. La
acumulación `double` determinista en orden Y→X. `sinc(0)=1` y los enteros no nulos se resuelven con una
tolerancia absoluta `1e-12` antes de evaluar `sin(πx)/(πx)`, evitando residuos dependientes de la
plataforma; la procedencia conserva tolerancia cero y solo copia la procedencia fuente en una
coincidencia integral exacta dentro del dominio. Primero se limita geométricamente cada índice, después
se validan siempre los 36 taps y su segmento, se normaliza el kernel completo, se acumula el resultado,
se limita la amplitud a `[0,255]` y se redondea con `MidpointRounding.AwayFromZero`. No se renormaliza
ningún subconjunto válido: un solo `NoData` o cambio de segmento invalida el destino y puede ensanchar
visualmente un gap. La versión estructural del algoritmo es 3.

Lanczos permanece full-only: una petición con base incremental fuerza una única recuperación full por
la infraestructura latest-only existente. No existe fallback a otro kernel. En la comprobación
1024×1024, resultado vigente, candidato y scratch conservaron el pico rentado observado de
8.650.752 bytes, por debajo de los 16 MiB neutrales; owners, bytes, builds, leases y bloques terminaron
en cero. En la VM VMware de diagnóstico, Dry observó 109,44–116,92 ms para 256×256 y
825,71–934,11 ms para 1024×1024 en ejecuciones aisladas/matriz. Estos tiempos incluyen render y
liberación del owner, exceden las hipótesis iniciales y no son baseline ni autorizan cambiar
presupuestos; requieren una campaña normal con warmup, iteraciones y hardware aprobados.

La caracterización posterior conserva exactamente ese orden y semántica, pero prepara por coordenada
los seis índices clampados y seis pesos de cada eje; la coordenada original se conserva aparte para la
procedencia exacta. Las cuatro tablas tipadas se rentan de pools acotados, se contabilizan y se devuelven
en éxito, cancelación, excepción y fallo parcial de renta. Su coste lógico es `80 × (ancho + alto)`:
40.960 bytes en 256², 163.840 bytes en 1024² y 87.040 bytes por bloque B de 1024×64. Los buckets medidos
son 49.152, 196.608 y 104.448 bytes respectivamente. El pico B-Scan 1024² medido pasa a 8.755.200 bytes,
todavía bajo el límite neutral de 16 MiB.

En la misma VM VMware, BenchmarkDotNet con tres procesos, tres warmups y tres iteraciones midió el
pipeline B completo antes/después en 44,42/11,11 ms para 256² y 708,61/180,80 ms para 1024². La mejora
es aproximadamente 4×, pero 1024² equivale a unos 5,5 Hz y continúa lejos de 30 Hz. Son resultados
diagnósticos, no baseline. Lanczos-3 se ofrece en live como modo de alta calidad con cadencia reducida,
sin garantía de 30 Hz; también puede utilizarse con la adquisición detenida. `None` continúa siendo el
modo predeterminado y Bilinear/Bicubic son los candidatos interactivos. Latest-only evita acumular
trabajo, conserva visible el último raster confirmado mientras se calcula y descarta cualquier candidato
obsoleto antes de publicarlo. Stop y cierre cancelan en el siguiente checkpoint por fila y esperan las
barreras de región, owners, callbacks y worker. No existe fallback silencioso ni cambio de kernel.
Una convolución separable se descarta por ahora porque cambiaría el orden de acumulación. Separar en
microbenchmarks la validación y la acumulación combinadas queda como P3 de profiling futuro, no como
defecto funcional ni bloqueo de entrega.

La misma preparación por eje se aplica posteriormente a Nearest, Bilinear y Bicubic. Cada render calcula
una vez las coordenadas, índices clampados y pesos X/Y y los reutiliza en todo el raster; el bucle por
píxel conserva la validación completa y el orden de acumulación Y→X. Las tablas se rentan y contabilizan
de forma acotada y se devuelven en éxito, cancelación o excepción. En 1024², el pico rentado observado es
8.470.576 bytes para Bilinear y 8.552.496 bytes para Bicubic; ambos permanecen bajo 16 MiB. En una campaña
diagnóstica corta de una sola ejecución en la misma VM VMware (`2` warmups, `3` iteraciones), el pipeline
B full 1024² midió 5,232 ms para Nearest, 15,147 ms para Bilinear y 35,720 ms para Bicubic. Estos datos no
son baseline. El delivery conserva una única entrada de tablas mientras permanezca idéntica la
transformación relativa, aunque el origen absoluto avance al desplazarse un B-Scan lleno. La clave incluye
modo/opciones, dimensiones, viewport, longitudes fuente, coordenada fuente inicial relativa y pasos
normalizados. Un cambio retira la entrada anterior; leases con recuento impiden devolver sus arrays hasta
que termine el último render que la usa. Stop y cierre drenan los renders y devuelven también la entrada
vigente, dejando la contabilidad a cero.

En live, la malla fuente neutral se materializa en un owner acotado antes de ejecutar el kernel. Los
leases del ring se liberan inmediatamente después de esa copia, de modo que Nearest/Bilinear/Bicubic/
Lanczos no fijan slots durante el cálculo y no provocan pérdidas de adquisición. Tres owners separados
guardan intensidad, máscara/procedencia adquirida compartida y segmentos: añaden
`3 × anchoFuente × altoDestino` bytes lógicos (máximo 3 MiB en 1024²) y permanecen incluidos en el
techo neutral de 16 MiB. No cambia máscara, segmentos, procedencia ni resultados del kernel.

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
distinguibles. La preferencia de modo se persiste como configuración UI versionada por usuario en
`%LOCALAPPDATA%/UTStudio/settings.json`. Solo contiene `schemaVersion` y `bScan.resamplingMode`;
ausencia, corrupción, versión futura o valor desconocido vuelven de forma segura a `None` y se
diagnostican sin impedir adquisición, Stop ni cierre.

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

Quedan confirmados: semántica compatible de None, Nearest explícito, Keys `a=-0.5`, Lanczos-3 `a=3`, política
conservadora de todos los taps válidos, composición en Visualization.Core sin proyecto nuevo y
exclusión de Kriging del selector visual. Continúa pendiente la conversión de objetivos diagnósticos
de memoria/tiempo en gates sobre hardware acordado.
Variograma, parámetros, unidades y UX de incertidumbre de Kriging quedan para su propio ADR.
