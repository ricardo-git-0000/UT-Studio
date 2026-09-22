# Plan del primer incremento B-Scan reutilizable

Estado: diseño aprobado; Fase 0 documental completada. La implementación requiere autorización por
fase. La fuente normativa es [ADR 0013](../adr/0013-bscan-bounded-live-visualization.md).

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

## Fase 4 — Presentation

Entrada: protocolo neutral estable.

Alcance: BScanViewModel, coordinador global de selección, A-Scan Live/Selected, ReturnToLive, estados de
residencia, viewport A/B, afinidad UI y cierre. Proyectos previstos: Presentation, contratos neutrales
aprobados en Visualization.Core y pruebas Core/Presentation. Sin control WPF.

Salida: una resolución global; sustitución atómica y fallo conservador; live continúa bajo Selected;
versiones A/B diferentes y orden inverso; ninguna mutación fuera de `IUiDispatcher` ni callback tardío.

## Fase 5 — WPF y composición

Entrada: ViewModels y contratos neutrales estables.

Alcance: BScanControl, WriteableBitmap, overlays, interacción, intenciones de región, BScanWindow, DI,
cierre, accesibilidad, DPI y resize. Proyectos previstos: Visualization.Wpf, App.Wpf y pruebas WPF.

Salida: Visualization.Wpf solo referencia Core; sin elementos por muestra/columna ni bitmap propio
duplicado; eventos WPF STA y prioridad selección/pan; cerrar B conserva A/sesión; cerrar Main espera
barreras; DPI 96/144 y controles estrechos validados.

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
