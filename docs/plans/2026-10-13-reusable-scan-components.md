# Plan por fases — Componentes WPF reutilizables A-Scan, B-Scan y preparación C-Scan

Fecha: 2026-10-13. Estado: Fase documental y encapsulaciones A-Scan/B-Scan implementadas; C, extracción 2D
y NuGet siguen pendientes. Fuente: [ADR 0016](../adr/0016-reusable-complete-scan-components.md).

## Objetivo y límites

Entregar componentes WPF completos insertables sin reconstruir ViewModel, adaptador, toolbar,
comandos, paneles, interacción o lifecycle. Consumirán solo puertos neutrales, con una autoridad RF
externa, y preservarán `TraceIdentity`, ownership, leases, cancelación, cierre y presupuestos vigentes.

Fuera de alcance: implementar C-Scan; persistencia; GPU; PA; gates; tiles; nuevas bibliotecas; y
proyectos, paquetes o referencias en Fase 0.

## Inventario exacto vigente

### Neutral compartible

| Área | Ubicación actual | Papel |
| --- | --- | --- |
| Identidad y autoridad | `Contracts/Inspection/InspectionModels.cs`, `InspectionPorts.cs` | `TraceIdentity`, revisiones, leases y puertos de traza/rango/estado |
| A visual | `Visualization.Core/AScan*`, `UtTraceAScanSelection.cs`, `ScanTimeViewport.cs` | proyección, snapshot, cursores, latest-only y selección autoritativa |
| B visual | `Visualization.Core/BScan*` | ingreso de commits, rango residente, raster, diagnósticos y fuente visual |
| 2D | `Visualization.Core/Scan2D/*` | geometría, máscara/procedencia, interpolación, firmas y buffers acotados |
| navegación | `Presentation/SharedScanTimeViewport.cs`, `Visualization.Core/ScanTimeViewport.cs` | coordinación temporal neutral, hoy dividida entre proyectos |
| selección A/B | `Presentation/BScanSelectionCoordinator.cs` | resolución exacta por `TraceIdentity` |
| presentación | `Presentation/AScanViewModel.cs`, `AScanViewState.cs`, `BScanViewModel.cs`, `BScanViewState.cs`, `BScanResamplingPresentation.cs` | UI-neutral; B es específico; A mezcla presentación con responsabilidades host |

### WPF compartible y específico

| Área | Ubicación actual | Clasificación |
| --- | --- | --- |
| Renderizadores | `Visualization.Wpf/Controls/AScanControl.cs`, `BScanControl.cs` | bajo nivel, públicos, específicos por scan en ensamblado común |
| Coordenadas/ejes | `Visualization.Wpf/Rendering/AScanCoordinates.cs` y auxiliares internos | primitivas candidatas; extraer solo con segundo consumidor |
| Paletas | `Visualization.Wpf/Rendering/BScanPalette.cs`, `BScanPaletteFile.cs` | WPF 2D/B; la carga de archivo es política de host |

A-Scan específico: snapshots/proyección/delivery/cursores en Core; ViewModel/estado en Presentation;
control y eventos en Visualization.Wpf; toolbar, bindings, panel y handlers en `MainWindow.xaml(.cs)`.
`AScanViewModel` contiene hoy `IApplicationSession`, configuración, creación de run, Start/Stop y
`SessionSnapshot`: en la extracción, esas responsabilidades y su chrome quedan en el host; cursores,
viewport, Live/Selected, estado visual, delivery status y estadísticas van al scope del componente.

B-Scan específico: ingress/delivery/modelos/opciones/memoria/auditoría/diagnósticos en Core;
ViewModel/estado/remuestreo y coordinación de selección en Presentation; control/bitmap/paleta en WPF;
vista completa en `BScanWindow.xaml(.cs)`, adaptación en `BScanControlAdapter`, preferencia y conversor
en App.Wpf.

### Integración exclusiva de UT-Studio

- `DesktopRuntime`: crea store, deliveries, sesión, viewport, selección, ViewModels y orden global.
- `ApplicationSession`: único lector, commit autoritativo y notificación A/B tras el commit.
- `MainWindow`: shell, Start/Stop, salida, apertura B y alojamiento A.
- `BScanWindowManager`: cardinalidad/cierre de ventanas; `BScanWindow` es shell, no componente.
- `ShutdownCoordinator`, `ShutdownInteractionGate`, rutas de preferencia y diálogos: políticas host.
- `InspectionDataStoreRegistry`/`InMemoryInspectionDataStore`: implementación y ownership host.

### Anexo exhaustivo de archivos productivos en alcance

Contratos/autoridad: `src/UTStudio.Contracts/Inspection/InspectionModels.cs`,
`InspectionPorts.cs`; `src/UTStudio.Inspection.Core/InspectionDataStoreRegistry.cs`,
`InMemoryInspectionDataStore.cs`; `src/UTStudio.Application/ApplicationSession.cs`.

Core neutral: `AScanPoint.cs`, `AScanSnapshot.cs`, `IAScanProjector.cs`, `AScanProjector.cs`,
`AScanCursorState.cs`, `AScanDeliveryStatus.cs`, `AScanDeliveryStatistics.cs`,
`AScanVisualDelivery.cs`, `UtTraceAScanSelection.cs`, `AdaptiveAxisTicks.cs`,
`ScanTimeViewport.cs`, `DeliveryStatusStore.cs`, `BScanModels.cs`, `BScanOptions.cs`,
`BScanIngress.cs`, `BScanDiagnostics.cs`, `BScanProjectionState.cs`, `BScanVisualModels.cs`,
`BScanVisualMemory.cs`, `BScanVisualAudit.cs`, `BScanVisualDiagnostics.cs`,
`BScanVisualDelivery.cs`, `Scan2D/Scan2DModels.cs` y `Scan2D/Scan2DResampler.cs`, todos bajo
`src/UTStudio.Visualization.Core/`.

Presentación neutral: `src/UTStudio.Presentation/AScanViewState.cs`, `AScanViewModel.cs`,
`BScanViewState.cs`, `BScanViewModel.cs`, `BScanSelectionCoordinator.cs`,
`BScanResamplingPresentation.cs`, `SharedScanTimeViewport.cs`, `IUiDispatcher.cs`,
`UiAsyncCommand.cs`, `UiLifetime.cs`, `SafePresentationPrimitives.cs` y
`CleanupFailureException.cs`.

WPF reutilizable: `src/UTStudio.Visualization.Wpf/Controls/AScanControl.cs`,
`BScanControl.cs`, `Rendering/AScanCoordinates.cs`, `BScanPalette.cs` y `BScanPaletteFile.cs`.

Integración App.Wpf: `MainWindow.xaml`, `MainWindow.xaml.cs`, `BScanWindow.xaml`,
`BScanWindow.xaml.cs`, `Adapters/BScanControlAdapter.cs`, `BScanResamplingModeConverter.cs`,
`Services/BScanWindowManager.cs`, `BScanResamplingPreferenceStore.cs`, `VisualMetrics.cs`,
`WpfUiDispatcher.cs`, `RefreshGate.cs`, `ShutdownInteractionGate.cs`, `ShutdownCoordinator.cs`,
`Composition/DesktopRuntime.cs`, `DesktopRuntimeFactory.cs` y `DesktopHostLifetime.cs`.

Los `.csproj`, `AssemblyInfo.cs`, `App.xaml(.cs)` y `StartupFailureWindow.cs` se inspeccionaron para
grafo, recursos y arranque, pero no contienen comportamiento A/B que deba migrarse.

## Matriz de dependencias

### Actual

| Proyecto | Depende de | Uso scan |
| --- | --- | --- |
| Contracts | Domain | identidad y puertos autoritativos |
| Inspection.Core | Contracts, Domain | store concreto |
| Application | Contracts, Domain | sesión/commit/adquisición |
| Visualization.Core | Contracts, Domain | A/B/Scan2D neutrales |
| Presentation | Visualization.Core, Contracts, Domain, Toolkit.Mvvm | ViewModels/coordinación |
| Visualization.Wpf | Visualization.Core, WPF | controles de bajo nivel |
| App.Wpf | todos los anteriores, Simulator, Hosting | composición y vistas completas fragmentadas |

### Propuesta mínima

| Unidad lógica candidata | Puede depender de | No puede depender de |
| --- | --- | --- |
| Core neutral común | Contracts, Domain | WPF, Application, Inspection.Core, hardware |
| WPF común | Core neutral | Presentation global, App.Wpf, store concreto |
| A por scan (neutral + WPF) | WPF/Core comunes, UI neutral mínima, Toolkit | B, App.Wpf, sesión/store concreto; VM nunca en WPF |
| B por scan (neutral + WPF) | WPF/Core comunes, UI neutral mínima, Toolkit | A, App.Wpf, sesión/store concreto; VM nunca en WPF |
| CScan.Wpf futuro | comunes aprobados tras ADR C | A/B obligatorios, hardware/store concreto |
| Host | paquetes elegidos + Application/Inspection/hardware | — |

No crear una unidad hasta que un movimiento concreto demuestre contenido y consumidores; un paquete
puede agrupar varios assemblies. No exponer
`IInspectionDataStore`: inyectar roles mínimos. La API, responsabilidades y lifecycle candidatos están
en ADR 0016; `DataContext` y service locator no serán API, y toda selección cruzará como
`TraceIdentity`, nunca como píxel o índice local. El estado B debe publicar atómicamente identidad
mostrada, `ResidentRange`, `RepresentedRange`, dirty region, revisiones y mapa seleccionable.

## Fases incrementales

### Fase 0 — propuesta documental (esta tarea)

ADR 0016, este plan, índice ADR y sincronización mínima de arquitectura. Revisiones en solo lectura de
solution_architect, ut_visualization, wpf_mvvm_specialist, quality_reviewer y repository_reviewer.
Gate: aprobación explícita antes de código.

### Fase 1 — caracterización

Congelar API y conducta: bindings/comandos, selección B→A, viewport, cursores, remuestreo, resize/DPI,
errores, prioridad Stop/cierre, cierre múltiple y callbacks tardías. Registrar baseline reproducible y
criterio de no regresión para admisión de Stop, inicio/final de cierre, comando UI bajo raster continuo,
invalidación repetida y máximos de trabajos/owners/callbacks. Inventariar gestos, foco, tab order,
AutomationProperties/peers, lector de pantalla, alto contraste, localización, recursos y FlowDirection.
Los oráculos incluyen reentrada inmediata desde `PropertyChanged`, measure/arrange y callbacks WPF,
snapshot atómico de capacidad/viewport/resolución/revisión y exactamente una full resync por
invalidación explícita; configuración de carga y límites quedan registrados junto al baseline.
Añadir test-host que evidencie dependencias. Sin movimientos. Gate: matriz/ownership aprobados, suite
verde, límites de recursos y comparación contra baseline.

### Fase 2 — encapsulación A-Scan

Crear scope A completo y mover toolbar/panel/adaptación visual desde MainWindow sin cambiar RF.
MainWindow conserva Start/Stop, configuración, run y shell; esas acciones no son toolbar del scan.
Gate: host de prueba instancia A sin `ApplicationSession` ni store concreto;
paridad funcional y cierre sin regresión.

Resultado 2026-10-13: implementado mediante `UTStudio.ScanVisualization.AScan.Wpf`, contexto neutral,
scope visual propio, `AScanControl` compuesto, integración en MainWindow y host demo independiente.
Start/Stop, sesión, store, B-Scan, métricas globales y shutdown permanecen en el host.

### Fase 3 — encapsulación B-Scan

Crear `BScanView` con adaptador, toolbar, interacción y estado; `BScanWindow` queda como shell. Separar
diálogo/ruta de paleta y preferencias mediante puertos opcionales. Gate: B insertable en cualquier
Window, selección exacta y coordinación explícita; `ResidentRange`/`RepresentedRange` preservados.

Resultado 2026-10-14: implementado mediante `UTStudio.ScanVisualization.BScan.Wpf`. `BScanView`
compone ViewModel, adaptador, renderizador, toolbar, selección, remuestreo, estado y cierre; la ventana
productiva queda como host fino. `TraceSelectionScope` sustituye el nombre público específico de B y
mantiene `TraceIdentity` exacta sin poseer sesión, store ni RF. Se añade un host demostrador separado.

P2.3 se valida contra el modelo síncrono real. El adaptador recorre `Created → Prepared/Attached →
Active → Closing → Closed`; Prepared puede registrar recursos internos, pero no publica callbacks ni
estado conectado y su rollback es completo. Active linealiza por CAS tras aplicar el estado/raster
inicial e inmediatamente antes del commit síncrono que publica DataContext, bindings y estado de
`BScanView`; así toda observación reentrante de esa publicación ya ve Active. La precondición exige un
`BScanControl` sin `RasterSource` previo.

La admisión del token bajo gate es el punto de linealización de la visita. Visita protegida, copia a
staging, revalidación y commit son atómicos y síncronos en el dispatcher; solo la reentrada del callback
puede intercalarse. Closing linealiza el rechazo de visitas nuevas y Closed espera el retorno de la ya
admitida. El productor latest-only mantiene una construcción activa y una sola pendiente reemplazable:
descarta la activa sustituida al retornar y procesa únicamente la última solicitud.

Matriz P2.3: (a) reentrada que invalida token, inicia cierre, rechaza una segunda visita y deja terminar
la primera; (b) activa latest-only retenida lógicamente con varias posteriores coalescidas y solo la
última publicada; (c) fallo de construcción restaura el control vacío; (d) fallo de actualización en
Active conserva el bitmap, bytes e identidad no nulos confirmados. Las barreras externas entre visita,
staging y commit, y el cierre externo dentro de ese tramo, se eliminan expresamente: no aplican porque
el dispatcher productivo no puede suspender ese tramo síncrono; no se contabilizan como pruebas
omitidas.

### Fase 4 — extracción 2D común

Caracterizar B y mover solo primitivas semánticamente genéricas del grid regular. Modelos de
traza/columna, wrap, ledger/pooling B quedan
fuera. Medir memoria, asignaciones, cancelación y respuesta bajo carga. Gate: común sin supuestos B,
sin WPF en Core y pruebas productivas de prioridad Stop/cierre/UI.

Diseño previo 2026-10-15: el [inventario de primitivas compartidas](../architecture/shared-scan-primitives-design.md)
concluye que no debe iniciarse aún una extracción productiva ni crearse un assembly común Scan WPF.
Las primitivas neutrales y gráficas comunes demostradas ya están en Core, Presentation o
Visualization.Wpf; `WpfUiDispatcher` es la única copia literal, insuficiente por sí sola para una nueva
unidad de despliegue. Bitmap, staging, rollback, estilos y helpers de lifecycle se reconsiderarán tras
el ADR de C-Scan. Esta conclusión está pendiente de aprobación antes de cualquier cambio de código.

### Fase 5 — aplicación WPF mínima de demostración

Aplicación separada con A y B y fuente sintética neutral: uso individual/conjunto, coordinación
opcional, errores y cierre. No referencia `UTStudio.App.Wpf` ni hardware. Gate: instrucciones
reproducibles y cero reconstrucción manual de ViewModel/adaptador/UI.

Resultado 2026-10-15: implementada `UTStudio.ScanVisualization.Wpf.Demo`. Aloja A y B simultáneamente,
usa una autoridad/store y un scope selección/viewport por grupo, mantiene Start/Stop en el host,
confirma antes de proyectar y demuestra `Sequence != AcquisitionIndex`. B puede cerrarse y reabrirse
sin detener A ni el productor. La matriz WPF añade A/B solos, orden de creación/cierre, reapertura,
selección exacta/latest-only, ReturnToLive, viewport bidireccional, fallos aislados y dos scopes sin
contaminación. El host cierra Views antes de coordinadores y autoridad.

Duplicación inventariada y aplazada: `WpfUiDispatcher`; esqueleto one-shot de Context/Loaded/Close;
publicación defensiva; accesibilidad/estilos; wiring de comandos. Solo el dispatcher es casi literal,
pero extraerlo exigiría introducir una dependencia WPF común por una clase mínima y anticiparía C.
Lifecycle, comandos, estilos y semántica de error difieren. No se extrae nada en esta fase.

### Fase 6 — preparación para C-Scan

Solo documentación de unidades/sistema de coordenadas, paso/extensión de celda, grid uniforme o
posiciones explícitas, cobertura/identidad por celda, rango dinámico, segmentación, firma, puertos
regionales y presupuesto. ADR C propio
antes de código; C no es B rotado. Gate: requisitos confirmados y autorización independiente.

### Fase 7 — NuGet posterior

Con consumidor externo real, decidir nombres, assemblies, recursos XAML, SemVer, TFM y metapaquete sin
código. Todo paquete nuevo requiere aprobación. Gate: API estable, demo, documentación y auditoría.

## Aceptación, compatibilidad y versionado

- A/B se insertan con contexto neutral; el host no crea su ViewModel, adaptador ni UI interna.
- Los controles de bajo nivel continúan disponibles y no poseen RF.
- Ningún neutral referencia WPF; ningún paquete scan referencia hardware, Application, Inspection.Core,
  App.Wpf ni store concreto.
- Store, gaps y revisiones mantienen una autoridad; memoria/colas/leases siguen acotados.
- Fuente prestada y scope poseído son modalidades explícitas; consumer ID, presupuesto, viewport y
  barrera de cierre tienen propietario documentado. Dos Views pueden compartir fuente y dos grupos
  A/B permanecen aislados.
- El cierre global respeta Stop, drain, sellado, `FinalRevision`, reconciliación y freeze antes de
  disponer Views/deliveries/store; cancelar la espera de `CloseAsync` no cancela cleanup.
- Selección exacta conserva `TraceIdentity`; dirty rectangle solo delimita raster.
- Invalidación geométrica produce una full resync; live se coalesce; callbacks tardías no publican;
  Stop/cierre/comandos responden bajo carga.
- Paridad UT-Studio: Start/Stop, cursores A, Live/Selected, B→A, navegación, remuestreo, paletas,
  ventanas, accesibilidad, DPI y cierre.
- La demo no referencia App.Wpf. C no existe hasta ADR propio.

Cada fase mueve una frontera sin cambiar a la vez API, algoritmo y ownership. Fachadas temporales solo
con criterio de retirada. En NuGet, SemVer por paquete; DP, comandos, resource keys, errores, threading
y ownership son contrato. El metapaquete fija versiones compatibles sin código.

## Riesgos

| Prioridad | Riesgo | Mitigación/gate |
| --- | --- | --- |
| P1 | doble autoridad/copia RF | puertos mínimos, auditoría de leases e identidad/revisión |
| P1 | callbacks o leases vivos al cerrar | contexto activo inmutable, generaciones, cancelación y drain |
| P1 | reentrancia publica parámetros mixtos | commit coherente antes de bindings y regresiones WPF |
| P1 | render priva Stop/cierre/UI | colas acotadas, coalescing y pruebas de latencia |
| P1 | WPF no invoca disposal o Context cambia tras activar | CloseAsync esperado por host; Unloaded solo pausa; coerción/factoría one-shot y pruebas de shutdown/reparenting |
| P1 | disponer consumidor/delivery antes de revisión final | bloquear UI; Stop/drain/seal/reconcile/freeze; después Dispose |
| P2 | fronteras finas o common inflado | extraer solo con segundo consumidor y revisar grafo |
| P2 | XAML/theming se vuelve API accidental | claves documentadas y pruebas de recursos |
| P2 | coordinación A/B singleton | scope explícito; dos grupos independientes en pruebas |
| P2 | Window→UserControl pierde conducta | shell host + matriz de paridad y puertos opcionales |
| P3 | nombres no escalan a C | nombres provisionales, sin abstracción anticipada |
| P3 | empaquetado prematuro | posponer hasta demo y consumidor real |

## Decisiones pendientes

1. Dirección de paquetes por scan con comunes mínimos.
2. Conservar Contracts/Visualization.Core actuales, sin Contracts visual nuevo por ahora.
3. Factoría frente a `Context`, y ownership de fuente/consumer/presupuesto/viewport; Dispatcher queda en WPF.
4. Theming, accesibilidad, atajos y extensión pública de toolbar/paneles.
5. Errores/logging y preferencias/paleta como puertos opcionales.
6. Nombres definitivos después de caracterización.

## Propiedad y validación de Fase 0

El agente principal es único escritor. Los cinco revisores son de solo lectura y devuelven gravedad,
evidencia y recomendación. Validar diff/nuevos, enlaces, índice, UTF-8 y coherencia con ADR y
arquitectura. Compilación y pruebas funcionales no aplican: no cambia código, proyectos ni paquetes.
