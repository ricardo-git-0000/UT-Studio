# Plan del primer incremento B-Scan reutilizable

Estado: diseño aprobado; Fases 0–5 implementadas.
La fuente normativa es [ADR 0013](../adr/0013-bscan-bounded-live-visualization.md).

## Reglas comunes

El incremento entrega B-Scan RF en vivo, acotado y reutilizable, selección exacta hacia A-Scan
Live/Selected, tiempo compartido A/B y ventana secundaria. Mantiene un único lector, memoria
presupuestada, Presentation neutral y Visualization.Wpf dependiente solo de Visualization.Core.

Cada fase entra con la anterior aprobada y validada. Su salida exige formato del alcance, build Release,
suites pertinentes, pruebas deterministas repetidas según riesgo, `git diff --check`, referencias y
árbol completo. `.vscode/` preexistente se preserva y queda fuera. Son una Fase 0 documental y cinco
fases de implementación; ninguna fase anticipa código de la siguiente.

## Fase 0 — Documentación

Entrada: diseño, contratos y precisiones aprobados.

Entregables: ADR 0013; cambios mínimos en ADR 0006, 0007, 0008 y 0012; arquitectura, pipeline,
requisitos y este plan.

Salida: enlaces y contenido consistentes, `git diff --check`; ningún código, proyecto, paquete o commit.

## Fase 1 — Contratos, integridad y fan-out

Entrada: Fase 0 aprobada.

Alcance: `AcquisitionIndex`, `ScanSpatialCoordinate?`, nueva política de integridad, diagnósticos,
fan-out aislado y simulador con coordenada nula. Proyectos previstos: Domain, Contracts, Application,
Acquisition.Simulator, composición mínima App.Wpf y pruebas Core/Application. Sin store B ni WPF B.

Salida: ownership/liberación única; gaps aceptados; duplicados, descensos, timestamps e índices inválidos
rechazados y contados; configuración incompatible terminal; fallo de una rama no detiene la otra; sin
lector competidor ni alias retenido.

## Fase 2 — Store e ingress B-Scan

Entrada: contratos y fan-out estables.

Alcance: ring preasignado, presupuestos, generaciones, leases, rechazo de slot fijado, ingress mínimo,
worker coalescido, índice/reducción, proveedor de trazas y copia exacta pooled. Proyectos previstos:
Visualization.Core y pruebas Core; sin ViewModel ni WPF.

Salida: exclusión real lectura/escritura; cierre drena leases y devuelve buffers; 32 MiB respetados con
N=65.535; provider copia exactamente, cancela y devuelve owners sin acumularlos; métricas p50/p95/p99/máximo listas
para escenarios reproducibles.

## Fase 3 — Entrega visual neutral

Entrada: store, leases y proveedor validados.

Alcance: snapshots/regiones neutrales y su lifetime, revisiones, dirty ranges, wrap, resincronización,
viewport espacial, límite de 30 Hz, instrumentación y carga. Proyectos previstos: Visualization.Core,
pruebas Core y documentación de rendimiento solo para resultados medidos. Sin Presentation ni WPF.

Salida: dirty nunca aplicado sobre base incompatible; saltos/wrap/cambios de región fuerzan resync;
ingestión, índice, sustitución y publicación medidos por separado; consumidor lento no causa crecimiento.

Implementada con viewport de origen entero y offsets decimales, raster bipolar neutral 8-bit,
revisión visual que avanza al confirmar el índice, snapshots/update latest-only a 30 Hz y proveedor
pull de regiones con ownership explícito y máximo de dos owners. Las regiones se revalidan contra run,
revisión y wrap después de rasterizar; incompatibilidades fuerzan full refresh y los gaps quedan NoData.
No incorpora ViewModel, control WPF, selección A/B ni composición, que permanecen en Fases 4 y 5.

## Fase 4 — Presentation

Entrada: protocolo neutral estable.

Alcance: BScanViewModel, coordinador global de selección, A-Scan Live/Selected, ReturnToLive, estados de
residencia, viewport A/B, afinidad UI y cierre. Proyectos previstos: Presentation, contratos neutrales
aprobados en Visualization.Core y pruebas Core/Presentation. Sin control WPF.

Salida: una resolución global; sustitución atómica y fallo conservador; live continúa bajo Selected;
versiones A/B diferentes y orden inverso; ninguna mutación fuera de `IUiDispatcher` ni callback tardío.

Implementada con coordinador global latest-selection-wins, metadata original en la copia exacta,
modos A-Scan `NoData`/`Live`/`Selected`, retorno inmediato al último live compatible y un
`BScanViewModel` neutral. Cada B registra su propio consumidor temporal y fabrica el binding con su
versión local. El estado B publica solo token y descriptor; el ViewModel retiene internamente el owner
pooled y ofrece copia síncrona validada de `AffectedRegion`, sin arrays raster en estados históricos ni
`IDisposable` para la vista. Delivery acumula incrementales desde la base mostrada de cada consumidor y
aplica cuota global de 16 MiB por bytes rentados, con dos consumidores y dos owners por consumidor. En
reset espacial sigue el rango residente; tras zoom o pan conserva y limita
la vista hasta otro reset. Control, eventos WPF y composición permanecen en Fase 5.

## Fase 5 — WPF y composición

Entrada: ViewModels y contratos neutrales estables.

Alcance: BScanControl, WriteableBitmap, overlays, interacción, intenciones de región, BScanWindow, DI,
cierre, accesibilidad, DPI y resize. Proyectos previstos: Visualization.Wpf, App.Wpf y pruebas WPF.

Salida: Visualization.Wpf solo referencia Core; sin elementos por muestra/columna ni bitmap propio
duplicado; eventos WPF STA y prioridad selección/pan; cerrar B conserva A/sesión; cerrar Main espera
barreras; DPI 96/144 y controles estrechos validados.

La Fase 5 queda implementada con `BScanControl` y conversión LUT a staging BGRA32 pooled acotado.
Los full se publican mediante bitmap candidato y los incrementales mediante backup/rollback de la región;
ningún callback externo se ejecuta durante `WriteableBitmap.Lock`. App.Wpf visita síncronamente la región
neutral bajo token y adapta las intenciones. El pico activo WPF máximo es aproximadamente 12 MiB por
control a 1.024×1.024, independiente del presupuesto neutral de 16 MiB.
La ventana B puede cerrarse/reabrirse mientras ingress y delivery continúan. La selección resuelve
`AcquisitionIndex` en el store y nunca presupone que coincide con `Sequence`. Los ejes usan ticks
neutrales 1/2/5. La localización de discontinuidades queda aplazada hasta disponer de metadata neutral
tipada y acotada; este incremento conserva el aviso global.

La integración productiva detectó y corrigió el bootstrap de consumidores: registrar B con un store ya
activo, o indexar la primera columna después del registro, encola metadata inicial y permite solicitar
el full sin depender de una fuente visual de pruebas.

La Tanda 2 endurece exclusivamente ciclo de vida y cierre: todas las etapas B se intentan una vez,
los fallos conservan principal y secundarios, A cierra antes de disponer la sesión y Host solo se
dispone tras cleanup obligatorio confirmado. El timeout de cinco segundos sigue siendo diagnóstico;
un fallo no confirmado mantiene MainWindow visible y `Completed=false`. No anticipa la Tanda 3.

La corrección consolidada de Tandas 1 y 2 materializa productivamente tres fases: `Stop` cierra
admisión, `WaitForDrainAsync` espera workers, callbacks, operaciones, leases y owners, y
`DisposeAsync` destruye solo tras drenaje confirmado. Las devoluciones fallidas del store se propagan
después de intentar todos los bloques y los owners raster prestados nunca se liberan por fuerza.

## Validación transversal

- Identidad, configuración, gaps, duplicados, descensos, índices y timestamps.
- Fan-out, aislamiento, ownership y liberación única.
- Presupuestos, overflow, bloques parciales, wrap, leases y cierre.
- RF bipolar, extremos `Int16`, picos y reducción.
- Selección exacta, eviction, nueva run, cierre y latest-wins.
- Revisión base, dirty, resync y coalescencia a 30 Hz.
- Versiones A/B distintas, intersección vacía y navegación bidireccional.
- UI, comandos, ventana secundaria, DPI y resize.
- Sin sleeps, delays ni sondeo como sincronización de pruebas.

## Archivos previstos por fase

| Fase | Archivos o áreas orientativas |
| --- | --- |
| 1 | `Domain/Acquisition/ScanSpatialCoordinate.cs`; `Contracts/Acquisition/ConventionalUtFrame.cs`; `Contracts/Presentation/IConventionalFrameSink.cs`; `Application/ApplicationSession.cs`; fan-out Application; simulador y pruebas |
| 2 | `Visualization.Core/BScanTraceStore.cs`, `BScanIngress.cs`, identidades/descriptores, leases, índice, `IBScanTraceProvider.cs` y pruebas |
| 3 | `Visualization.Core/BScanSnapshot.cs`, regiones raster, revisiones/dirty ranges, viewport espacial, delivery y pruebas/carga |
| 4 | `Presentation/BScanViewModel.cs`, coordinador de selección, estados A-Scan Live/Selected, comandos y pruebas Presentation |
| 5 | `Visualization.Wpf/BScanControl.cs`, render/coords; `App.Wpf/BScanWindow.xaml(.cs)`, composición/owner; pruebas WPF |

Los nombres son una previsión, no amplían las referencias permitidas ni obligan a crear un tipo si la
fase demuestra que una responsabilidad existente puede alojarlo sin acoplamiento.

## Riesgos y aplazamientos

El coste de copia exige benchmark antes de prometer configuraciones máximas. Un slot fijado puede
producir rechazo B visible. El índice de 64 muestras y presupuestos son ajustables con evidencia.
Archivo real, encoder, eje físico, almacenamiento, GPU, PA y paquetes adicionales quedan fuera.
