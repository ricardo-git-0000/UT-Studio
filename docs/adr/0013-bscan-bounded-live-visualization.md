# ADR 0013 — B-Scan en vivo acotado, selección exacta y ventana secundaria

Fecha: 2026-09-22. Estado: Aceptado; Fases 1–5 implementadas.
Trazabilidad: R01–R09, R13 y R14; concreta Q12 y la parte visual de Q13. Complementa
[ADR 0006](0006-session-window-lifecycle.md), [ADR 0007](0007-frame-source-ownership.md),
[ADR 0008](0008-latest-only-visual-delivery.md), [ADR 0009](0009-reusable-scan-visualization-controls.md)
y [ADR 0012](0012-shared-scan-time-navigation.md).

## Contexto y alcance

El primer B-Scan reutilizable representa A-Scans convencionales RF `Int16` ordenados por adquisición.
El eje horizontal inicial es `AcquisitionIndex`, sintético y adimensional; nunca se presenta como
distancia. El eje vertical es el tiempo físico de muestra. El simulador no publica posición física.

El incremento incluye acumulación en vivo acotada, paleta bipolar, navegación espacial y temporal,
selección de una columna para presentarla en el A-Scan, modos A-Scan Live/Selected, ventana B-Scan
secundaria y navegación temporal bidireccional A/B. Se aplazan encoder real, eje espacial físico,
profundidad, velocidad de material, gates, PA, multicanal, selección de área, gestos táctiles,
persistencia y sincronización entre grupos de scans independientes.

Esta decisión se implementará mediante la Fase 0 documental y cinco fases de código posteriores. Sus
criterios están en el [plan](../plans/2026-09-22-bscan-first-increment.md).

## Identidad, metadata e integridad

Cada frame añade al contrato existente `AcquisitionIndex` obligatorio y
`ScanSpatialCoordinate?` opcional. Son datos variables por frame y pertenecen a
`ConventionalUtFrame`, no a `ConventionalUtFrameMetadata`, que continúa siendo compartida por la run.
No se crea un envelope separado: mantener identidad, muestras, índice y posición en el mismo objeto
evita desalineación y conserva una única transferencia de ownership.

`AcquisitionIndex` es un ordinal `ulong`, empieza en cero por run y es estrictamente creciente. No se
renumera por rechazo visual, gap ni expulsión. En el simulador inicial coincide con `Sequence`; otros
productores deben asignarlo explícitamente. Un índice duplicado o descendente se rechaza y contabiliza
como integridad per-frame, independientemente de `Sequence`. Una posición física futura solo afecta a
`SpatialCoordinate`, nunca al orden del índice.

La coordenada opcional contiene únicamente valor finito, unidad y eje tipados. Procedencia, calidad,
calibración y geometría se añaden solo cuando exista un caso confirmado. En el simulador inicial es
`null`. `Sequence` comprueba la producción; `AcquisitionIndex` ordena el B-Scan; `ColumnSlot` es
efímero; `SnapshotVersion` y `StoreRevision` versionan publicaciones y no identifican trazas. La
identidad estable combina fuente, run, canal físico y secuencia. El descriptor y la selección incluyen
también el índice para validar localización, pero no como segunda fuente de unicidad. Las transformaciones
restan un origen entero antes de convertir índices a `double`, evitando pérdida de precisión sobre 2^53.

La sesión sustituirá su rechazo terminal de cualquier discontinuidad por esta política:

| Caso | Política |
| --- | --- |
| Gap ascendente | Aceptar el frame actual, contar exactamente los ausentes y publicar discontinuidad |
| Secuencia duplicada | Rechazar y liberar el frame, contabilizar y continuar la run |
| Secuencia descendente | Rechazar y liberar, contabilizar fuera de orden y continuar |
| Timestamp descendente | Rechazar y liberar, contabilizar y continuar |
| AcquisitionIndex duplicado/descendente | Rechazar y liberar, contabilizar y continuar |
| Posición repetida | Conservar como columna diferente |
| Salto espacial | Conservar y marcar discontinuidad |
| Retroceso espacial | Conservar en orden de adquisición y abrir un segmento |
| Configuración incompatible | Fallo de configuración; cerrar la run y exigir otra configuración/run |

La integridad usa watermarks separados: última secuencia observada hacia delante y último timestamp
aceptado para entrega. Rechazar un timestamp descendente avanza la secuencia observada, de modo que el
siguiente frame no vuelve a contar como ausente uno que sí se recibió, pero no rebaja ni avanza el
timestamp aceptado. Duplicados y descensos no reducen el máximo observado. `ulong.MaxValue` se trata
sin calcular `previous + 1`; cualquier secuencia posterior es inválida.

Los diagnósticos distinguen `AcquisitionMissingFrames`, `DuplicateFramesRejected`,
`OutOfOrderFramesRejected`, `TimestampRegressionsRejected`, `InvalidAcquisitionIndexFramesRejected`, rechazos B por
causa, trabajo visual sustituido, publicaciones, renderizados y expulsiones. Un frame ausente antes de
Application, uno recibido pero no admitido por B, una actualización visual coalescida y una expulsión
normal del ring nunca comparten significado ni contador.

## Contratos públicos previstos

La Fase 1 añade en Domain `ScanSpatialCoordinate`, `ScanSpatialUnit` y `ScanAxisId`. Contracts
amplía `ConventionalUtFrame` y el préstamo visual:

```csharp
public ulong AcquisitionIndex { get; }
public ScanSpatialCoordinate? SpatialCoordinate { get; }

void IConventionalFrameSink.OpenRun(ConventionalUtFrameMetadata metadata);
void IConventionalFrameSink.Accept(
    ConventionalUtFrameMetadata metadata,
    ulong sequence,
    ulong acquisitionIndex,
    TimeSpan elapsedSinceRunStart,
    ScanSpatialCoordinate? spatialCoordinate,
    ReadOnlySpan<short> samples);
ValueTask IConventionalFrameSink.WaitForCloseAsync(AcquisitionRunId runId);
```

La identidad visual en Visualization.Core será `(SourceId, RunId, PhysicalChannelId, Sequence)`.
`BScanTraceDescriptor` conserva además la `ConventionalUtFrameMetadata` original y añade
`AcquisitionIndex`, elapsed, muestreo, posición opcional, segmento y
discontinuidades. `IBScanTraceProvider.GetTraceAsync(identity, cancellationToken)` devolverá un
`BScanTraceResult` discriminado. `BScanSnapshot`, `BScanRasterRegionSnapshot` y
`BScanVisualUpdate` llevarán siempre identidad local, versión y revisión. Ningún contrato visual B
se añade a Contracts: pertenece a Visualization.Core y conserva el grafo de referencias.

## Fan-out y presupuesto síncrono

`ApplicationSession` continúa como único lector del `ChannelReader`. Un fan-out visual síncrono invoca
primero A y después B y presta el mismo `ReadOnlySpan<short>` a ramas aisladas en `OpenRun`, `Accept`
y `CloseRun`. `OpenRun` completa antes de que puedan comenzar `Accept` o `CloseRun`; un `CloseRun`
sí puede solaparse con un `Accept` ya admitido y debe invalidar su resultado. Así el coste B no retrasa la admisión A del mismo frame más allá del retorno posterior
del fan-out. Un fallo de
una rama la diagnostica y cierra una vez para esa run sin impedir invocar o continuar la sana. Tampoco
cambia la liberación exactamente una vez del frame por Application. Este fan-out no es el futuro
contrato lossless de procesamiento o persistencia.

Dentro de `BScanIngress.Accept` solo se permite validar, reservar un slot ya preasignado, copiar una
vez las muestras al almacenamiento propio, confirmar metadata/revisión y emitir una señal no
bloqueante. No realiza índice completo, reducción, tiles, raster, WPF ni callback UI. El umbral inicial
de 1 ms es exclusivamente diagnóstico, por configuración; se registran p50, p95, p99, máximo y
excesos. No constituye garantía y debe validarse mediante benchmarks y carga.

El backpressure de memoria se aplica antes de abrir la run: se validan y reservan los bloques. Durante
`Accept` no se espera por memoria, worker ni UI. Si el siguiente slot está fijado por un lector, B
rechaza esa columna con `BScanColumnsRejectedPinnedSlot`, no cambia slot, generación ni revisión y
marca una discontinuidad B para la siguiente columna admitida; A continúa. El worker se despierta con
una señal coalescida de capacidad uno. La ingestión puede superar la frecuencia visual; trabajo
intermedio sustituido se contabiliza y no se denomina pérdida de adquisición.

## Ring, generaciones y leases

El `BScanTraceStore` posee un ring preasignado de trazas completas. Cada slot tiene generación y un
recuento de leases de lectura. Worker, proveedor de selección y lectura regional adquieren bajo la
puerta interna un lease que valida slot, generación e identidad e incrementa el pin antes de exponer
memoria. Los leases son internos y nunca salen por el proveedor público. El escritor nunca sobrescribe
un slot fijado y nunca espera por él. Una segunda comprobación de generación no sustituye el pin.

Liberar el último lease vuelve a habilitar el slot. Se publican `ActiveReadLeases`,
`MaximumReadLeases`, `BScanColumnsRejectedPinnedSlot` y leases liberados durante el cierre. El cierre
impide nuevos leases, cancela operaciones propias y espera asíncronamente que `ActiveReadLeases` llegue
a cero antes de devolver arrays. Los cinco segundos de cierre siguen siendo solo diagnóstico: no
autorizan reutilizar memoria aún leída.

El puerto abre con metadata completa para reservar antes del primer préstamo. `CloseRun` invalida de
forma acotada y `WaitForCloseAsync` es la barrera que Application cruza antes de admitir otra run; una
arena retirada no coexiste así con otra reserva de 32 MiB en la misma instancia.

## Presupuestos de memoria

`TraceMemoryBudgetBytes` se aplica a la longitud realmente rentada, incluidos redondeo de `ArrayPool`
y bloques parcialmente ocupados. Los bloques se rentan progresivamente al abrir y cada longitud real
se carga antes de aceptar el bloque. El que excedería el límite se devuelve. La rama abre con capacidad
reducida cuando alcanza `MinimumColumns`, inicialmente 128; si no, devuelve todo y falla antes de
iniciar. Toda aritmética de productos y sumas es `checked`. La capacidad efectiva es el mínimo entre
`MaximumColumns` y las columnas de bloques completos realmente admitidos.

| Muestras × columnas | Bytes RF | MiB |
| --- | ---: | ---: |
| 2.048 × 512 | 2.097.152 | 2,000 |
| 2.048 × 1.024 | 4.194.304 | 4,000 |
| 65.535 × 128 | 16.776.960 | ≈16,000 |
| 65.535 × 256 | 33.553.920 | ≈32,000 |
| 65.535 × 512 | 67.107.840 | ≈63,999 |
| 65.535 × 1.024 | 134.215.680 | ≈127,998 |

Valores iniciales: 32 MiB para trazas, 16 MiB visuales, máximo configurable de 1.024 columnas,
objetivo de 512, raster máximo 1.024 × 1.024 e índice min/max inicialmente por 64 muestras. Para
65.535 muestras, con el bucket esperado de 524.288 `short`, un bloque de ocho columnas ocupa 1 MiB y
32 bloques permiten 256 columnas dentro de 32 MiB. `ArrayPool` no garantiza ese tamaño: si devuelve
arrays mayores, la capacidad efectiva disminuye o la apertura falla por no alcanzar `MinimumColumns`.

Buffers visuales controlados que pueden coexistir:

| Categoría | Máximo de diseño |
| --- | ---: |
| Metadata y slots, 1.024 × límite contable de 128 B | 128 KiB |
| Índice min/max de trazas grandes | 4 MiB |
| Tiles neutrales 8-bit, 1.024 × 1.024 | 1 MiB |
| Un `WriteableBitmap` BGRA32 | 4 MiB |
| Staging de una columna/dirty rect pequeño | 4 KiB |
| Selección vigente y candidato simultáneos | hasta 256 KiB |
| Descriptores, revisiones y margen | 1 MiB |
| Total visual controlado | ≈10,379 MiB |

La derivación por escenario usa el bucket esperado, ancho raster igual al número de columnas hasta
1.024 y selección vigente+candidato:

| Muestras × columnas | RF | Metadata | Índice | Tile | Bitmap | Staging | Selección | Margen | Total coexistente |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2.048 × 512 | 2,000 | 0,063 | 0,063 | 0,500 | 2,000 | 0,004 | 0,008 | 1,000 | ≈5,637 MiB |
| 2.048 × 1.024 | 4,000 | 0,125 | 0,125 | 1,000 | 4,000 | 0,004 | 0,008 | 1,000 | ≈10,262 MiB |
| 65.535 × 128 | 16,000 | 0,016 | 0,500 | 0,125 | 0,500 | 0,004 | 0,250 | 1,000 | ≈18,394 MiB |
| 65.535 × 256 | 32,000 | 0,031 | 1,000 | 0,250 | 1,000 | 0,004 | 0,250 | 1,000 | ≈35,535 MiB |
| 65.535 × 512 | 63,999 | 0,063 | 2,000 | 0,500 | 2,000 | 0,004 | 0,250 | 1,000 | ≈69,816 MiB |
| 65.535 × 1.024 | 127,998 | 0,125 | 4,000 | 1,000 | 4,000 | 0,004 | 0,250 | 1,000 | ≈138,377 MiB |

La tabla no afirma que todos los escenarios quepan en los defaults: con 32 MiB de trazas, N=65.535
queda limitado aproximadamente a 256 columnas. El presupuesto visual excluye RF y suma las demás
categorías; nunca supera ≈10,379 MiB en estos escenarios lógicos.

WPF mantiene un solo bitmap visible. Antes de bloquearlo materializa `AffectedRegion` en staging BGRA32
rentado. Un refresco completo usa además un bitmap candidato y lo publica solo tras completarlo; un
incremental conserva un backup pooled del rectángulo y restaura byte a byte ante un fallo controlado.
No se ejecutan callbacks externos con el bitmap bloqueado y la identidad avanza solo después del commit.
El peor pico activo a 1.024×1.024 es aproximadamente 12 MiB por control: 4 MiB visible, 4 MiB staging
y 4 MiB de candidato full o backup incremental. Este presupuesto WPF es independiente de los 16 MiB
neutrales; `ArrayPool` puede retener buckets después de devolverlos. Las copias del compositor son
overhead de plataforma no controlable. Presentation rechaza antes de solicitarla una región cuyas dimensiones
y buffers contables excedan 16 MiB; WPF aplica además el mismo límite de píxeles antes de recrear el
bitmap. Una configuración imposible o con menos de un bloque/columna efectiva falla antes de iniciar.

## Procesamiento, regiones y frecuencia visual

El RF original es la autoridad. El índice conserva mínimo y máximo por grupo configurable. Para una
celda coloreada se elige determinísticamente el extremo de mayor valor absoluto; en empate, el primer
índice. Un píxel nunca se usa para reconstruir señal exacta.

Índices, tiles y publicaciones se producen fuera de `Accept`. La admisión de publicaciones neutrales y
la aplicación de datos nuevos al bitmap tienen un máximo configurable inicial de 30 Hz: intervalo
mínimo de 333.334 ticks de `TimeSpan`, medido con `TimeProvider`, sin catch-up. Resize, DPI o navegación
pueden repintar inmediatamente el mismo estado; WPF no garantiza la frecuencia real de composición.
Se miden columnas ingeridas/indexadas, trabajo sustituido, publicaciones, aplicaciones al bitmap y
renders por separado.

`Visualization.Core` produce `BScanRasterRegion` neutral con identidad de stream y consumidor, versión
local, revisión base y resultante, rectángulo, dimensiones, formato y payload propio. Presentation
retiene internamente como máximo la región visible y un candidato y publica únicamente un token opaco
y metadata. `TryVisitPresentedRegion` valida token, stream, run y generación y expone síncronamente solo
`AffectedRegion` al adaptador; este convierte a staging WPF fuera del lock del bitmap. Un token antiguo devuelve
`false` y solicita fuera de la puerta una recuperación full coalescida; un destino insuficiente lanza
`ArgumentException`. El estado observable no contiene arrays, `Memory`, owners ni `IDisposable`, por lo
que los estados históricos no retienen raster ni generan una copia LOH por publicación. `BScanControl`
no consulta el store, no coordina trabajo
asíncrono y no referencia Application ni Presentation. Zoom/resize emite una intención neutral;
Presentation solicita y publica otra región.

El registro de un consumidor nuevo inicia además una petición bootstrap mínima cuando el store ya tiene
estado visual. Si la primera columna llega después del registro, `VisualStateChanged` inicia la misma
petición. La entrega conserva el último snapshot por consumidor y lo reproduce a una suscripción tardía.
Esto rompe el ciclo en el que Presentation necesitaba metadata para pedir render y Delivery necesitaba
una petición para publicar metadata; la primera región visible sigue siendo un full coherente.

## Revisiones y resincronización

Cada columna confirmada incrementa `StoreRevision`. Una actualización contiene `RunId`, versión de
snapshot, revisión base, revisión resultante, rango residente, dirty range, generación de wrap y marca
de refresco completo. Un dirty range solo se aplica si la revisión mostrada coincide con la base.

Run/stream distinto, base no reconstruible, wrap, región expulsada, zoom/pan, resize, DPI, paleta o
nivel fuerzan resincronización. Core produce una región coherente bajo leases sin mantener la puerta
del store durante rasterización costosa y valida identidad/revisión antes de publicarla. Un resultado
obsoleto se descarta. WPF recrea bitmap solo por dimensión física, DPI, formato o run.

La Fase 3 distingue la revisión de admisión interna de la revisión visual publicada. Esta última avanza
al confirmar cada índice y es la que ocupan `BaseStoreRevision` y `StoreRevision` en el protocolo
visual: una columna todavía no indexada no puede declararse representada. El nombre se conserva como
revisión del store visual, pero nunca identifica una traza ni sustituye su `BScanTraceIdentity`.
`WrapGeneration` publicado corresponde al contenido indexado, no a trabajo RF todavía pendiente.

La región neutral usa `BipolarIntensity8`: `0` significa exclusivamente NoData, `1` representa
`short.MinValue`, `128` representa cero y `255` representa `short.MaxValue`; los intervalos negativos
y positivos se mapean monótonamente. La reducción escoge el mayor valor absoluto y, en empate, la
primera columna por `AcquisitionIndex` y la primera muestra. Los gaps permanecen NoData y no se
interpolan. El raster se obtiene mediante `IBScanRasterRegionProvider`; la notificación observable solo
transporta metadata versionada, evitando compartir ownership entre varios observadores.

La coalescencia normal no fuerza full refresh. Cada consumidor publica su propia base mostrada; Delivery
une las revisiones `(base, target]` todavía residentes, aunque haya 1, 8, 33, 100 o más revisiones entre
publicaciones. Las bases de consumidores rápidos y lentos son independientes. Si falta una revisión,
hubo wrap o cambió la firma de render, se produce full refresh. `SnapshotVersion` es local al consumidor
y no se usa para probar continuidad del store.

El viewport espacial conserva un origen `ulong` y offsets `decimal`; resta el origen antes de cualquier
conversión de raster y limita zoom a 4096× con al menos una columna visible. Core admite inicialmente dos
consumidores raster configurables y dos owners por consumidor. Los consumidores se registran por
`consumerId`; el presupuesto visual global inicial de 16 MiB descuenta la reserva fija y carga la
longitud real del owner antes de transferirlo. Cada consumidor tiene además un techo proporcional que
impide monopolizar el presupuesto. Una solicitud no admitida devuelve `ResourceLimit`; el cierre libera
owners y desregistra la cuota. Un owner transferido continúa válido tras wrap hasta su liberación.
Mientras Presentation está en reset espacial, el viewport sigue el rango residente que crece o avanza.
El primer zoom o pan desactiva ese autoajuste; `ResetSpatial` lo reactiva.

## Interacción, inspección y accesibilidad

La rueda sin modificador amplía tiempo sobre Y alrededor del puntero; Ctrl+rueda amplía el eje espacial
X. Botón central o Mayúsculas+arrastre izquierdo hacen pan 2D. Clic izquierdo selecciona columna; una
selección/cursor activo tiene prioridad sobre el pan izquierdo. Doble clic en zona libre y `Home`
restablecen ambos viewports. `Escape`, pérdida de captura o `Unloaded` terminan sin intención tardía.
Hay comandos separados de reset, zoom, pan y `ReturnToLive`, bindings dependientes del foco y
propiedades de automatización.

El cursor de inspección informa `AcquisitionIndex`, posición solo si existe, tiempo y amplitud
aproximada de la celda reducida. La UI muestra rangos temporal/espacial visibles, Live/Selected y
Resident/Evicted/NotAvailable. La amplitud exacta pertenece a la traza seleccionada, no al raster. La
paleta usa cero neutro y familias distinguibles para negativos/positivos, con clamp y mapeo definido de
`short.MinValue` y `short.MaxValue`.

## Selección exacta y A-Scan Live/Selected

`IBScanTraceProvider` es neutral respecto al almacenamiento y permite implementación live/ring y una
futura desde archivo. Devuelve `Available`, `NotAvailable`, `Evicted`, `Cancelled` o `Faulted`; no se
implementa archivo ahora. `Evicted` significa que una identidad conocida ya no reside. En una selección
ya confirmada, Evicted es solo estado de residencia B y no invalida el snapshot A.

La implementación live conserva tombstones acotadas a la capacidad efectiva del ring. Una identidad
expulsada más antigua que esa ventana vuelve a `NotAvailable`; no se infiere `Evicted` desde Sequence,
porque gaps y rechazos B nunca residieron. Las copias exactas independientes están limitadas a dos;
una tercera solicitud espera de forma cancelable antes de fijar un slot.

Existe como máximo una resolución activa para el grupo A/B. Una solicitud nueva cancela la anterior y
vence mediante generación monotónica. Se valida identidad, run, slot y generaciones antes y después de
copiar y antes de publicar. El candidato se copia completamente a un owner pooled propio antes de ser
observable. Si queda obsoleto o falla, se dispone sin sustituir la selección válida anterior.

El coordinador es propietario exclusivo del candidato y la traza seleccionada. ViewModel y control no
reciben memoria que pueda devolverse mientras siga observable. El `AScanSnapshot` publicado posee sus
propios puntos y sigue válido después de disponer la traza exacta. La traza anterior solo se dispone
después de sustituir el estado en UI y cruzar la barrera de callbacks/render.

```text
NoData --live compatible--> Live
Live --selección válida--> Selected
Live --selección fallida--> Live
Selected --nueva válida--> Selected(nueva)
Selected --fallo/cancelación--> Selected(anterior)
Selected --expulsión--> Selected + Residency=Evicted
Selected --ReturnToLive--> Live inmediato
Live|Selected --nueva run--> Live compatible o NoData
Cualquier estado --cierre--> Closing --> Closed
```

El último live continúa actualizándose durante Selected. Zoom/pan no recopian RF. Nueva run cancela
resolución, selección y viewport incompatibles. Cerrar B no borra una selección confirmada mostrada por
A; cerrar MainWindow dispone toda selección y espera la barrera.

## Viewport compartido, fronteras y ventanas

Una instancia de `SharedScanTimeViewport` gobierna el intervalo físico común. Mantiene dominios por
consumidor, pero cada ViewModel crea su `ScanTimeViewportBinding` con `RunId` y `SnapshotVersion`
locales. Nunca se comparan versiones A y B. A orienta tiempo en X y B en Y; el viewport espacial B es
independiente.

Visualization.Core contiene modelos, stores, reducción, regiones y transformaciones neutrales.
Presentation contiene estado observable, solicitudes, coordinadores y ViewModels. Visualization.Wpf
solo renderiza e interpreta input y continúa referenciando únicamente Visualization.Core. App.Wpf
compone fan-out, servicios y ventanas.

Composición posee sesión, deliveries/stores y coordinadores de selección y tiempo. Los ViewModels no se
referencian. Cada BScanWindow posee su ViewModel y consumer ID, tomando prestados los servicios
compartidos. Cerrar B cancela sus operaciones UI, retira el consumidor y cruza su barrera sin detener
sesión ni A. El delivery/store B, propiedad de composición y de vida de sesión, continúa ingiriendo
para permitir reabrir la ventana con datos recientes. MainWindow cierra secundarias/ViewModels,
sesión, workers/deliveries, selección, viewport y Host. Si la sesión termina primero, no se admiten
selecciones nuevas; una ya materializada puede mostrarse como histórica.

El cierre propietario de B intenta de forma independiente adaptador, ViewModel, retirada del consumidor,
estado UI, liberación de región y barrera. Conserva el primer error y los secundarios sin omitir etapas;
llamadas repetidas o concurrentes comparten resultado. Si `Show()` falla, la composición ejecuta ese
mismo cierre antes de devolver el fallo. Una factory interna permite pruebas productivas con fuente
controlada y opciones de ring pequeñas sin introducir contratos de prueba en Core o Presentation.

El cierre de composición usa tres transiciones internas monotónicas: cerrar admisión y solicitar
parada, esperar el drenaje real, y destruir recursos. La barrera B espera worker, callbacks,
operaciones raster y todos los owners transferidos; estos últimos solo los libera su propietario.
El ingress intenta devolver todos los bloques una vez y propaga conjuntamente cualquier devolución
fallida, que nunca se contabiliza como cierre confirmado. La fase destructiva no comienza si la
barrera anterior falla o permanece pendiente.

En WPF, Presentation conserva el owner pooled y publica un token. App.Wpf realiza una visita síncrona
neutral a `BipolarIntensity8` bajo ese token y Visualization.Wpf convierte únicamente `AffectedRegion`
a staging local BGRA32 antes del commit transaccional. La vista no recibe `IDisposable`. La continuidad incremental
se valida con stream, wrap, revisión base y configuración de render, no comparando versiones A/B.

## Riesgos y aplazado

Copiar hasta 131.070 bytes dentro de `Accept` puede afectar al lector; los percentiles deben medirse
antes de prometer caudal. Los rechazos por slot fijado son visibles, pero una futura exigencia de cero
rechazos requerirá otro diseño. Índice de 64 muestras, presupuestos y 30 Hz son configurables y sujetos
a benchmark.

Se aplazan proveedor de archivo real, recuperación de expulsadas, encoder, procedencia/calidad,
calibración, eje X físico, pirámide LOD completa, GPU, paquetes externos, profundidad, gates y
sincronización espacial entre grupos.

La discontinuidad localizada queda aplazada: el contrato actual solo aporta `HasDiscontinuities` y no
distingue de forma autoritativa gaps de secuencia, saltos espaciales, rechazo o wrap. Su futura metadata
será neutral, tipada, agrupada por viewport y acotada; no se inferirá del raster. Los ticks adaptativos
sí se resuelven en Core con offsets relativos, pasos 1/2/5 y cantidad máxima, dejando el formato en WPF.
