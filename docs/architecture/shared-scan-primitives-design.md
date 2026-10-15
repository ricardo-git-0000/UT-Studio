# Diseño de primitivas compartidas para componentes Scan

Fecha: 2026-10-15. Estado: propuesta documental pendiente de aprobación.

Trazabilidad: desarrolla la extracción aplazada por [ADR 0016](../adr/0016-reusable-complete-scan-components.md)
y la Fase 4 del [plan de componentes](../plans/2026-10-13-reusable-scan-components.md). No autoriza
cambios de código, proyectos, referencias o paquetes ni constituye el diseño de C-Scan.

## Objetivo, alcance y propiedad

El objetivo es separar duplicación demostrada de coincidencias superficiales entre los componentes
completos A-Scan y B-Scan, sin anticipar una jerarquía A/B/C. El agente principal es el único escritor
de esta fase; los revisores especializados son lectores. Se preservan autoridad RF, `TraceIdentity`,
leases, budgets, cancelación y orden de cierre vigentes.

La clasificación usada es:

- **extraer ahora**: equivalencia semántica, al menos dos consumidores reales, frontera existente
  adecuada, pruebas directas y ahorro superior al coste de dependencia;
- **mantener separado**: comportamiento específico o coincidencia pequeña cuyo acoplamiento sería
  más costoso que duplicarlo;
- **reconsiderar tras diseñar C**: C aportaría la tercera evidencia necesaria, especialmente en 2D,
  bitmap, staging, accesibilidad y chrome de componente.

## Resumen de decisión

No se recomienda crear ahora un ensamblado común específico de componentes Scan. Tampoco se propone
ninguna extracción productiva inmediata. Las primitivas neutrales realmente comunes ya viven en
`UTStudio.Visualization.Core` o `UTStudio.Presentation`; las primitivas WPF de bajo nivel ya comparten
`UTStudio.Visualization.Wpf`. La única copia literalmente equivalente pendiente es `WpfUiDispatcher`,
pero por sí sola no justifica una nueva unidad de despliegue y no puede trasladarse a
`UTStudio.Visualization.Wpf` sin introducir allí una dependencia de `UTStudio.Presentation`.

La siguiente fase de código, si se aprueba, debe comenzar por caracterización y medición, no por mover
tipos. El gate para crear un futuro `UTStudio.ScanVisualization.Wpf` común será disponer de al menos
dos primitivas WPF+Presentation literalmente equivalentes y estables, o de un tercer consumidor C
diseñado que confirme esa frontera. A y B deben seguir instalables por separado.

## Grafo de proyectos

### Actual

```text
Contracts ───────────────┐
Domain ──────────────────┼─> Visualization.Core
                         ├─> Presentation ───────────────┐
Visualization.Core ──────┘                               │
Visualization.Core ─────────> Visualization.Wpf          │
                                                         │
Presentation + Visualization.Wpf ─> AScan.Wpf            │
Presentation + Visualization.Wpf ─> BScan.Wpf <──────────┘

Demo A ─> AScan.Wpf
Demo B ─> BScan.Wpf + Inspection.Core
Demo conjunta ─> AScan.Wpf + BScan.Wpf + Inspection.Core
```

`AScan.Wpf` y `BScan.Wpf` no se referencian entre sí. `Visualization.Wpf` no referencia
`Presentation`. Los proyectos neutrales no referencian WPF.

### Propuesto ahora

El grafo permanece idéntico. Se documentan contratos internos candidatos, sin crear nodos ni aristas.

### Alternativa condicionada, no aprobada

```text
Presentation + Visualization.Wpf
              └─> ScanVisualization.Wpf.Common
                    ├─> AScan.Wpf
                    ├─> BScan.Wpf
                    └─> CScan.Wpf (solo después de su ADR)
```

Esta alternativa es admisible solo si el contenido común supera `WpfUiDispatcher`, no contiene
renderizado específico y no obliga al consumidor de A a instalar B/C. Nunca se invertirá la flecha
para que `Visualization.Wpf` dependa de `Presentation`.

## Matriz de ubicación por naturaleza

| Tipo | Ubicación candidata | Decisión |
| --- | --- | --- |
| Modelos/lifecycle neutral | `Presentation` o `Visualization.Core` | conservar los actuales; no crear lifecycle universal |
| Renderizado neutral | `Visualization.Core` | conservar; `Scan2D` solo se amplía con semántica C confirmada |
| Primitivas WPF sin Presentation | `Visualization.Wpf` | usar para dibujo, DPI, coordenadas y bitmap cuando sean realmente genéricas |
| Integración WPF + Presentation | futuro común Scan WPF | no crear ahora; gate de dos primitivas estables o C diseñado |
| Comportamiento específico | paquete A, B o futuro C | mantener por scan |

## Inventario común/específico

| Candidato | Archivos y símbolos exactos | Similitud, dependencias y ownership | Lifecycle | Posible C | Riesgo / ahorro real | Pruebas existentes | Recomendación |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Dispatcher WPF | `src/UTStudio.ScanVisualization.AScan.Wpf/WpfUiDispatcher.cs::WpfUiDispatcher`; `src/UTStudio.ScanVisualization.BScan.Wpf/WpfUiDispatcher.cs::WpfUiDispatcher` | Copias literales salvo namespace; adaptan un `Dispatcher` prestado a `Presentation.IUiDispatcher`. La ausencia de asignación inline es inferencia del código, pendiente de medición. | Vida del componente; no dispone el Dispatcher. | Sí | Riesgo bajo de drift; ahorro de una clase por paquete, sin reducir dispatches, locks ni buffers. | `AScanViewModelTests.StartStopCommandsExecuteOnceAndAllTheirEventsUseUiDispatcher`; `AScanViewTests.StandaloneViewOwnsVisualScopeAndPreservesInheritedDataContext`; `BScanViewTests.StandaloneViewActivatesCommandsAutomationDpiAndBalancesConsumer` | Mantener dos copias; candidato futuro común WPF+Presentation. Nunca mover a `Visualization.Wpf`. |
| Lifecycle neutral de ViewModel | `src/UTStudio.Presentation/UiLifetime.cs::UiLifetime`; `CleanupFailureException.cs` | `UiLifetime` sí es compartido; posee cola/gate y cada ViewModel posee su instancia. El cleanup estructurado está disponible, pero A/B no siguen idéntico protocolo. | Se cierra antes de la barrera del ViewModel. | Sí | Generalizar más puede añadir indirección; ahorro adicional nulo. | `AScanViewModelTests.ClosingInvalidatesQueuedAndLateCallbacksWithoutStoppingSession`; `BScanProductiveIntegrationTests.Active_store_survives_window_close_and_reopens_with_balanced_consumers` | Conservar en `Presentation`. |
| Lifecycle de componente WPF | `AScanView.OnContextChanged/OnLoaded/CloseAsync`; `BScanView.OnContextChanged/OnLoaded/StartActivation/ActivateAsync/CloseAsync`; `BScanControlAdapter.BScanAdapterLifecycle` | Solo coinciden one-shot Context, Loaded y Task de cierre. A activa síncronamente; B usa transacción, rollback y visitas protegidas. | A: implícito; B: `Created→Prepared→Active→Closing→Closed`. | Desconocido | P1 si una base debilita linealización B; ahorro limitado al boilerplate. | `AScanViewTests.ContextIsOneShotAndNewInstanceCanReopen`; `BScanViewTests.AdapterConstructionFailureRollsBackBeforeItIsObserved`, `RealViewReentrantInvalidationCloseRejectsSecondVisitAndDrainsFirst` | Mantener separado; rechazar lifecycle/base universal. |
| Contextos | `src/UTStudio.Presentation/AScanComponentContext.cs::AScanComponentContext`; `BScanComponentContext.cs::BScanComponentContext` | Records neutrales prestados, con fuentes y opcionalidad distintas; la View no dispone feeds/scopes. | Asignación one-shot; referencia descartada por host tras cerrar View. | Sí, con contrato propio. | Una interfaz común perdería obligatoriedad; ahorro meramente nominal. | `AScanViewTests.ContextIsOneShotAndNewInstanceCanReopen`; `BScanViewTests.ContextIsOneShotAndAClosedComponentCanBeReopenedAsANewInstance` | Mantener específicos como convención, no jerarquía. |
| Publicación segura | A: `src/UTStudio.Presentation/AScanViewModel.cs::AScanViewModel.OnPropertyChanged/NotifyRelayCommandsClosed`; B: `SafePresentationPrimitives.cs::SafeObservableObject.OnPropertyChanged/SafeRelayCommand/SafeParameterRelayCommand`; `AScanVisualDelivery.Subscription.RunAsync`; callbacks de `BScanVisualDelivery` | Garantía común con implementaciones distintas; gates/coalescing/drain de deliveries son específicos. | Válida hasta Closing; handlers fallidos se aíslan según propietario. | Sí | Unificar puede cambiar orden/error/asignaciones; ahorro potencial moderado, no medido. | `AScanViewModelTests.FailingPropertyAndCommandObserversDoNotStrandUpdates`; `AScanHostViewModelTests.ThrowingBindingIsDiagnosedAndLaterStateStillApplies`; `BScanViewTests.RealViewConnectedUpdateFailurePreservesConfirmedBitmapBytesAndIdentityThenRecovers` | Mantener separado; caracterizar antes de converger en Presentation. |
| Suscripciones y handlers | `AScanViewModel` campos `_sessionSubscription/_visualSubscription/_statusSubscription`; `BScanViewModel`; `AScanView.OnViewModelChanged`; `BScanView.OnViewModelChanged`; `BScanControlAdapter.RegisterControlEvents/RollbackConstruction` | Cada propietario desuscribe lo adquirido; B exige compensación por etapa, A no tiene adaptador. | Attach/close A; Prepared/Active/rollback B. | Sí, cardinalidad desconocida. | Una bolsa genérica ocultaría orden/errores; ahorro pequeño. | `AScanViewTests.StandaloneViewOwnsVisualScopeAndPreservesInheritedDataContext`; `BScanViewTests.StandaloneViewActivatesCommandsAutomationDpiAndBalancesConsumer`; `BScanViewTests.RealViewReentrantInvalidationCloseRejectsSecondVisitAndDrainsFirst` | Mantener separado. |
| Comandos | `UiAsyncCommand`; `AScanViewModel.StartCommand/StopCommand/ToggleCursorsCommand/ResetViewportCommand`; `BScanViewModel.ZoomTimeInCommand/PanSpatialLeftCommand/ResetAllCommand/ReturnToLiveCommand/Commands` | Contratos comunes; sets, CanExecute y protección no equivalentes. Los ViewModels poseen comandos. | Se invalidan al cerrar el ViewModel. | Sí para infraestructura. | Catálogo común impondría semántica; ahorro nulo. | `AScanViewModelTests.StartStopCommandsExecuteOnceAndAllTheirEventsUseUiDispatcher`; `BScanInteractionTests.Routed_wheel_click_and_double_click_emit_exact_intentions` | Conservar infraestructura neutral; toolbars específicos. |
| Accesibilidad/UIA | `src/UTStudio.ScanVisualization.AScan.Wpf/AScanView.xaml::AutomationProperties.Name/HelpText/LiveSetting`; `src/UTStudio.ScanVisualization.BScan.Wpf/BScanView.xaml::AutomationProperties.Name/HelpText/LiveSetting`; `AScanControl` y `BScanControl` declarados en esos XAML | Comparten convenciones de foco/nombre/live region; contenido e interacción específicos. | Pertenece a la View cargada; sin ownership externo. | Probable. | Base/peer común congelaría textos/tab order; ahorro pequeño. | `AScanViewTests.CursorCommandsEventsResizeDpiAndAutomationRemainAvailable`; `BScanViewTests.StandaloneViewActivatesCommandsAutomationDpiAndBalancesConsumer` | Mantener; reconsiderar guía/tokens con C. |
| Estilos y recursos | `src/UTStudio.ScanVisualization.AScan.Wpf/AScanView.xaml` atributos locales `Margin/Padding/Foreground`; `src/UTStudio.ScanVisualization.BScan.Wpf/BScanView.xaml::UserControl.Resources`; `BScanResamplingModeConverter` | No existe diccionario común estable; A usa valores locales, B palette/resampling. | Carga con la View. | Probable. | Resource keys/pack URI serían API accidental; ahorro mínimo. | `RenderTests.IntegratedSimulatorSnapshotDrawsWithoutPerPointVisuals`; `AScanViewTests.CursorCommandsEventsResizeDpiAndAutomationRemainAvailable`; `BScanViewTests.StandaloneViewActivatesCommandsAutomationDpiAndBalancesConsumer` | Aceptar duplicación visual. |
| Estado vacío/error/cierre | `AScanComponentPhase`; `BScanComponentPhase`; `AScanView.PublishState`; `BScanView.ComponentPhase/ComponentError` | Estados no isomorfos: B añade NoData/Selected/Closed y múltiples errores. | Publicado durante activación/Active/cierre propios. | Desconocido. | Enum común permitiría estados inválidos; ahorro nominal. | `AScanViewTests.PhaseErrorRecoveryAndViewportCommandsAreExposedByCompleteView`; `BScanViewTests.CloseAwaitsAdapterCleanupAndReportsItsFailure` | Mantener mappings específicos. |
| Viewport temporal | `src/UTStudio.Visualization.Core/ScanTimeViewport.cs`; `src/UTStudio.Presentation/SharedScanTimeViewport.cs` | Ya común y neutral; el scope posee dominios, las Views lo toman prestado. | Host dispone después de consumidores. | Sí si C tiene eje temporal. | Reutilización ya conseguida; generalizar a espacio sería incorrecto. | `ScanTimeViewportTests`; `JointScanComponentAcceptanceTests.ExactSelectionSharedViewportCloseOrdersAndReopenRemainIndependent` | Conservar. |
| Navegación espacial | `src/UTStudio.Visualization.Core/BScanVisualModels.cs::BScanSpatialViewport/BScanSpatialViewportOperations`; `src/UTStudio.Presentation/BScanViewModel.cs::ZoomSpatialAt/PanSpatialBy/ZoomSpatial/PanSpatial` | Específica de AcquisitionIndex B; C necesitará dos ejes/geometría. | Consumer B. | No directamente. | P1 si C hereda supuestos B; ahorro inexistente hoy. | `BScanInteractionTests.Routed_middle_and_shift_pan_capture_release_and_selection_has_priority`; `BScanRangeProjectionTests.HistoricalViewportReadsProviderWhileResidentRangeRemainsAuthoritative` | Mantener B; rediseñar con C. |
| Ejes y ticks | `src/UTStudio.Visualization.Core/AdaptiveAxisTicks.cs::AdaptiveAxisTicks.Create`; `AScanControl.OnRender`; `BScanControl.OnRender` | Generador neutral ya común; dibujo, orientación, unidades y márgenes difieren. | Sin recursos persistentes. | Generador sí. | Renderer común cambiaría apariencia A; ahorro pequeño. | `AdaptiveAxisTicksTests`; `CoordinatesTests.RfExtremesAndOffsetMapToViewportAndResize`; `RenderTests.IntegratedSimulatorSnapshotDrawsWithoutPerPointVisuals` | Conservar generador; no extraer renderer WPF. |
| Cursores y selección | `src/UTStudio.Visualization.Core/AScanCursorState.cs::AScanCursorState/AScanCursorOperations`; `src/UTStudio.Presentation/BScanSelectionCoordinator.cs::TraceSelectionScope`; `AScanControl.CursorActivated/CursorMoveRequested`; `BScanControl.HitSelection` | Medida visual aproximada A frente a selección exacta por `TraceIdentity`. | Cursores poseídos por VM A; scope de selección poseído por host. | C requerirá semántica propia. | P1 de identidad si se fusionan; ningún ahorro real. | `AScanCursorMeasurementsTests`; `UtTraceAScanSelectorTests`; `JointScanComponentAcceptanceTests.ExactSelectionSharedViewportCloseOrdersAndReopenRemainIndependent` | Mantener separados. |
| DPI y resize | `AScanControl.OnRenderSizeChanged/OnRender`; `BScanControl.OnRenderSizeChanged/OnDpiChanged/RequestRasterSize` | A redibuja vector; B renegocia raster físico acotado. | Control cargado; B dispara nueva solicitud. | C probablemente raster. | Helper común ocultaría coste de reraster; ahorro bajo. | `VisualizationComponentTests.CursorHitTestingResizeDpiVisibilityAndDragAreDeterministic`; `BScanControlTests.Narrow_control_renders_offscreen_at_96_and_144_dpi_without_children` | No extraer; comparar B/C tras ADR C. |
| Staging, rollback y bitmap | `src/UTStudio.Visualization.Wpf/Controls/BScanControl.cs::ApplyRaster/CommitFull/CommitIncremental`; campos staging/backup/candidate; `WriteableBitmap`; `BScanControlAdapter.RollbackConstruction` | Solo B; A dibuja snapshots inmutables. Ownership temporal explícito en B. | Candidato y staging nacen dentro de visita síncrona; commit transfiere bitmap visible; rollback restaura backup; finally libera buffers. | C probable, contrato desconocido. | P1 de bytes/owners/dirty range; gran ahorro potencial solo B/C. | `BScanControlTests.Failed_or_throwing_copy_preserves_surface_identity_and_pixels_then_recovers`; `BScanViewTests.RealViewActivationRollbackAfterInitialRasterRestoresEmptyControlExactly` | Reconsiderar tras ADR C. |
| Diagnósticos | `src/UTStudio.Visualization.Core/AScanDeliveryStatistics.cs::AScanDeliveryStatistics`; `AScanDeliveryStatus.cs`; `BScanDiagnostics.cs::BScanDiagnostics`; `BScanVisualDiagnostics.cs`; `BScanControlAdapter.BScanAdapterDiagnostics` | Comparten principio de balance, no campos ni dueño. | Nacen/mueren con delivery/adaptador. | Sí como principio. | Mega-record acoplaría paquetes; ahorro nulo. | `AScanVisualDeliveryTests.ThrowingObserverIsDetachedAndOtherObserverContinues`; `VisualDiagnosticTests.TerminalPublisherErrorReachesViewModelWithoutAnotherSnapshot`; `BScanViewTests.RealViewReentrantInvalidationCloseRejectsSecondVisitAndDrainsFirst` | Mantener específicos. |
| Ownership/disposición | remarks de `AScanComponentContext`/`BScanComponentContext`; `AScanView.DisposeAsync`; `BScanView.DisposeAsync`; `BScanVisualDelivery.ActiveConsumerCount`; `BScanIngress.Diagnostics.ActiveReadLeases` | Regla común: inputs prestados, recursos internos poseídos; barreras concretas distintas. | Host cierra Views antes de scopes/fuentes. | Sí. | Base puede disponer recursos prestados; ahorro limitado. | `AScanViewTests.StandaloneViewOwnsVisualScopeAndPreservesInheritedDataContext`; `BScanViewTests.StandaloneViewActivatesCommandsAutomationDpiAndBalancesConsumer`; `JointScanComponentAcceptanceTests.ComponentsSupportAloneTogetherAndEitherCreationOrder` | Documentar convención, no automatizar. |
| Demos | `samples/UTStudio.AScan.Wpf.Demo/MainWindow.xaml.cs`; `samples/UTStudio.BScan.Wpf.Demo/MainWindow.xaml.cs`; `samples/UTStudio.ScanVisualization.Wpf.Demo/MainWindow.xaml.cs` | Código no productivo; cada demo hace visible su grafo/ownership. | Host controla inicio, cierre y recursos. | Demo C futura. | Compartir ocultaría independencia; ahorro solo de scaffolding. | `AScanViewTests.ComponentAndDemoHaveNoApplicationOrStoreImplementationReferences`; `BScanViewTests.PublicApiAndProjectBoundariesRemainReusable`; `JointScanComponentAcceptanceTests.DemoAndPackagesKeepPublicArchitectureBoundaries` | Mantener duplicación deliberada. |
| Tests STA | `tests/UTStudio.Tests.Wpf/StaTest.cs::StaTest.Run`; `DiagnosticWait.cs::Until/For`; `ManualClock.cs::ManualClock`; `ProductiveBScanFixture.cs::ProductiveBScanFixture` | `StaTest`/`DiagnosticWait` ya comunes; reloj/fixture son B. | Por test; dispatcher termina al completar operación. | Sí para harness. | Paquete de tests añade mantenimiento; ahorro ya capturado localmente. | `AScanViewTests.StandaloneViewOwnsVisualScopeAndPreservesInheritedDataContext`; `BScanViewTests.RealViewReentrantInvalidationCloseRejectsSecondVisitAndDrainsFirst`; `JointScanComponentAcceptanceTests.TwoPairsUseIndependentSelectionAndViewportScopes` | Conservar en el ensamblado de tests. |

## Comparación de alternativas

| Alternativa | Ventajas | Costes y riesgos | Decisión |
| --- | --- | --- | --- |
| Mantener duplicación pequeña | Cero aristas nuevas, A/B instalables por separado, trimming simple | Dos copias del dispatcher y wiring menor | Elegida ahora |
| Usar `UTStudio.Visualization.Wpf` | Reutiliza assembly existente | Para el dispatcher exigiría `Visualization.Wpf → Presentation`, rompe la neutralidad y arrastra Toolkit/contratos | Rechazada para integración; válida solo para WPF gráfico sin Presentation |
| Crear común Scan WPF | Frontera correcta para WPF+Presentation; C podría reutilizarla | Assembly extra, API interna nueva, coste AOT/trimming y despliegue por una clase; tentación de acumular abstracciones | Diferida hasta cumplir gate |
| Usar `Presentation` neutral | Sin nuevo assembly; ya contiene lifecycle/comandos seguros | No puede contener Dispatcher, WPF, bitmap, Automation ni recursos | Elegida solo para primitivas neutrales actuales |
| Composición mediante helpers | Puede compartir piezas pequeñas sin herencia | Un helper genérico puede ocultar orden, rollback y ownership | Preferida cuando exista equivalencia; helpers estrechos y sin estado global |
| Clase base WPF A/B/C | Centraliza hooks aparentes | Herencia frágil sobre Loaded/DP/Dispatcher, fuerza lifecycle común, dificulta XAML/trimming y mezcla 1D/2D | Rechazada; no hay evidencia excepcional |

## Eficiencia y coste de dependencias

- `WpfUiDispatcher` no añade locks ni buffers; compartirlo ahorraría una clase por paquete, pero añadiría
  un assembly cargable, metadata, referencia y superficie de trimming. El ahorro de líneas no compensa.
- `UiLifetime` usa un lock y una cola por ViewModel; ya está compartido. Envolverlo agregaría llamadas y
  dificultaría razonar sobre la linealización sin reducir asignaciones.
- La publicación segura copia invocation lists y aísla handlers fallidos. Un wrapper adicional no debe
  insertar otro dispatch, Task o lock por evento.
- A es dibujo vectorial sin copia RF; B usa raster, staging, backup y owners acotados. Unificar su ruta
  caliente agregaría ramas virtuales/delegates y podría aumentar asignaciones o retención.
- Helpers WPF futuros serán `internal`, preferiblemente estáticos/sellados, sin reflexión, service
  locator ni eventos estáticos. Deben ser compatibles con trimming y no descubrir Views por tipo.
- Un consumidor de A no debe recibir B, Scan2D raster ni paletas. Un consumidor de B no debe recibir
  cursores A. Un común futuro solo podrá depender de Presentation y Visualization.Wpf, nunca de A/B.

## API interna propuesta, condicionada

No se crea en esta fase. Si se cumple el gate del assembly común, la primera API CLR sería estrecha y
permanecería `internal` mediante amistades explícitas desde el assembly común hacia los consumidores
`UTStudio.ScanVisualization.AScan.Wpf` y `UTStudio.ScanVisualization.BScan.Wpf` (y C solo cuando exista):

```csharp
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    internal WpfUiDispatcher(Dispatcher dispatcher);
    public bool CheckAccess();
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
```

Las amistades se limitarían a esos paquetes, nunca a App, demos o tests, y una prueba arquitectónica
impediría ampliarlas. Si esa amistad no se aprueba, la alternativa es mantener las copias: no se hará
público el tipo solo para justificar el movimiento.

No se propone `ScanViewBase`, `IScanView`, `ScanComponentContext`, `ScanComponentPhase`, registro global
de componentes ni contenedor genérico de subscriptions. Un helper posterior de key bindings o cleanup
solo se aceptará si A, B y C comparten exactamente orden, errores y ownership; su API deberá recibir
acciones explícitas y no poseer ViewModels, controles ni fuentes.

## Lista exacta de extracciones recomendadas

### Ahora

Ninguna extracción productiva. Sí se recomienda conservar y reconocer como comunes las primitivas ya
ubicadas correctamente: `AdaptiveAxisTicks`, `ScanTimeViewport*`, `SharedScanTimeViewport`,
`TraceSelectionScope`, `IUiDispatcher`, `UiLifetime` y `CleanupFailureException`. `SafeObservableObject`
y los safe commands son hoy infraestructura B reutilizable, no implementación compartida por A/B;
su convergencia se mantiene como candidato neutral condicionado.

### Tras evidencia adicional

1. `WpfUiDispatcher` a un común Scan WPF, solo al cumplirse el gate de contenido.
2. Política WPF raster B/C para DPI, bitmap candidato, staging y rollback, solo tras ADR C y benchmarks.
3. Recursos de accesibilidad/estilo, únicamente si C confirma tokens estables sin convertir textos o
   resource keys accidentales en API.
4. Helpers de activación/cierre, solo si C comparte el mismo protocolo transaccional; A no debe adoptar
   el protocolo B por uniformidad estética.

## Duplicaciones aceptadas

- dos `WpfUiDispatcher` internos literalmente equivalentes;
- wiring one-shot de `Context`, `Loaded`, `CloseAsync` y publicación de DependencyProperties;
- key bindings y toolbars específicos;
- nombres, help text, live regions, estilos locales y status panels;
- mapping específico de fase/error;
- transformaciones y dibujo de ejes por control;
- infraestructura sintética de cada demo.

## Secuencia propuesta de commits pequeños

No se ejecuta sin aprobación. Cada commit debe dejar la solución compilable:

1. `test(scan): characterize duplicated dispatcher and dependency boundaries` — equivalencia,
   referencias y medición reproducible de allocations/dispatch; sin mover producción.
2. `docs(scan): record no-extraction gate result` — ruta elegida si no hay masa crítica: registra métricas,
   conserva copias y cierra la fase sin nueva dependencia.
3. `refactor(scan): add common WPF integration assembly` — ruta condicionada si el gate se aprueba; proyecto vacío
   mínimo, referencias A/B y pruebas arquitectónicas.
4. `refactor(scan): centralize WPF UI dispatcher` — mover una clase, eliminar dos copias y verificar A/B.
5. `docs(scan): record measured extraction outcome` — actualizar ADR/plan con métricas y rollback.

Los commits 3–5 se omiten si el gate sigue sin cumplirse. La extracción raster B/C tendrá otro plan y
no se mezcla con esta secuencia.

## Migración y rollback

1. Congelar grafo, APIs públicas y pruebas de lifecycle/afinidad.
2. Añadir primero pruebas arquitectónicas y de equivalencia.
3. Introducir el común sin cambiar namespaces públicos ni conducta.
4. Migrar A y B en commits separados; medir build, artefactos, allocations y dispatch.
5. Eliminar copias solo después de ambas suites verdes.

Rollback: revertir primero cada consumidor a su copia interna; después retirar la referencia y el
assembly común. No se cambia serialización ni datos, por lo que no hay migración persistente. Nunca se
revierte mediante reescritura de historial y no se mezcla el rollback con cambios de C o Scan2D.

## Criterios de aceptación de una extracción futura

- cero cambios de API pública, XAML observable, comandos, fases, selección o ownership;
- A y B siguen instalables y ejecutables por separado;
- ningún proyecto neutral referencia WPF; `Visualization.Wpf` continúa sin `Presentation`;
- misma cantidad de dispatches, Tasks, locks y callbacks por operación en paths caracterizados;
- cero buffers, owners, leases o timers adicionales; balances y cierre permanecen deterministas;
- ninguna regresión de DPI/resize, UI Automation, trimming o build AOT documentado;
- tests A, B, aceptación conjunta y arquitectura verdes; demo individual y conjunta funcionales;
- reducción demostrada de mantenimiento/dependencias, no solo de líneas;
- rollback practicable por consumidor y sin cambio simultáneo de algoritmo.

## Riesgos

| Prioridad | Riesgo | Mitigación |
| --- | --- | --- |
| P1 | una base común debilita las barreras de cierre B o añade autoridad | prohibir base universal; ownership explícito y pruebas por propietario |
| P1 | generalizar bitmap copia/retiene RF o rompe budgets | no extraer raster antes de ADR C y benchmarks de owners/bytes |
| P1 | nueva dependencia introduce WPF en neutrales o Presentation en Visualization.Wpf | pruebas de grafo y regla de aristas |
| P2 | assembly común demasiado fino | gate de contenido y comparación de artefactos |
| P2 | helper agrega dispatch/Task/lock por callback | caracterización y conteos antes/después |
| P2 | estilos/resource keys se vuelven API accidental | mantener recursos locales hasta contrato de theming |
| P2 | C queda forzado al modelo B | ADR C independiente; selección, geometría y viewport propios |
| P3 | duplicación del dispatcher diverge | prueba de equivalencia o revisión mecánica mientras siga duplicado |
| P3 | demos duplican infraestructura | aceptarlo para preservar independencia observable |

## Decisiones y cuestiones pendientes

Hecho: existe un único duplicado literal (`WpfUiDispatcher`); los lifecycles A/B no son equivalentes y
la publicación segura A/B comparte garantía, pero no toda su implementación.
Decisión propuesta: no extraer producción ni crear assembly común en esta fase. Pendiente de aprobación:
el gate de creación del común Scan WPF y la secuencia de caracterización. Cuestión abierta: geometría,
selección, bitmap y lifecycle reales de C; no pueden resolverse antes de su ADR.
