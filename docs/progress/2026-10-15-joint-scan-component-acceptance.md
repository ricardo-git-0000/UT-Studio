# Aceptación conjunta de componentes A-Scan y B-Scan

## Objetivo

Demostrar que un host WPF externo a `UTStudio.App.Wpf` puede consumir simultáneamente los componentes
completos A y B mediante contratos neutrales y un scope compartido, sin adquirir autoridad RF.

## Resultado

Se añadió la demo `samples/UTStudio.ScanVisualization.Wpf.Demo`. Posee una sola autoridad de
inspección, confirma cada commit antes de notificar deliveries, usa `Sequence != AcquisitionIndex`,
y comparte `TraceSelectionScope` y `SharedScanTimeViewport`. Permite Start/Stop, selección B→A,
ReturnToLive desde los componentes, cerrar/reabrir B y cierre global ordenado.

## Decisiones confirmadas

- Los contextos A/B, selección y viewport son prestados a los componentes.
- El host crea, retiene y descarta referencias a los contextos; dispone coordinadores, deliveries,
  ingreso B, writer, store y reloj/timers.
- Cada View posee su adaptador, ViewModel y suscripciones privadas; los deliveries poseen callbacks y
  el ingreso B conserva consumers, raster owners y leases. Crear una View no transfiere ownership.
- Los componentes se cierran antes de disponer coordinadores y autoridad.
- No se extrae infraestructura común ni Scan2D en esta fase.

## Archivos modificados

- Nueva demo conjunta y alta en `UTStudio.sln`.
- Nueva matriz `JointScanComponentAcceptanceTests` y extensión diagnóstica del fixture B.
- ADR 0016 y plan de componentes actualizados.

## Pruebas y validaciones

La matriz cubre componentes solos y conjuntos, ambos órdenes, cierre/reapertura, selección exacta y
supersession, viewport, callbacks fallidos, scopes independientes y balances. La validación completa
terminó con build Release sin advertencias; Core 3/3 (560 pruebas por vuelta); WPF 3/3 (133 por vuelta);
matriz conjunta 20/20 (8 casos por vuelta); smoke de la demo con ventana visible, receptiva y cierre
ordenado; formato limitado; `git diff --check`; grafo de referencias e inventario de artefactos.

### Trazabilidad de la pirámide de pruebas

- Nivel neutral A: `AScanHostViewModelTests.ThrowingBindingIsDiagnosedAndLaterStateStillApplies`
  demuestra que un callback de binding fallido se diagnostica y que un estado posterior se aplica;
  `AScanViewModelTests.FailingPropertyAndCommandObserversDoNotStrandUpdates` cubre conjuntamente los
  callbacks de `PropertyChanged` y `CanExecuteChanged`, la continuidad de comandos y una actualización
  posterior. `AScanHostViewModelTests.InitialStateConnectsOnceAndDisposeDisconnectsOnce` y
  `AScanViewModelTests.InitialStateAndBorrowedSubscriptions` verifican los balances de las
  suscripciones neutrales que esos ViewModels poseen.
- Nivel WPF A: `AScanViewTests.StandaloneViewOwnsVisualScopeAndPreservesInheritedDataContext` verifica
  las suscripciones de sesión y visuales adquiridas por `AScanView`; `PhaseErrorRecoveryAndViewportCommandsAreExposedByCompleteView`
  verifica error, recuperación y balance del status feed a través del componente completo.
- Nivel WPF B: `BScanViewTests.RealViewConnectedUpdateFailurePreservesConfirmedBitmapBytesAndIdentityThenRecovers`
  atraviesa `BScanView`, adaptador, visita protegida, staging y control; inyecta el fallo de copia,
  conserva bitmap/bytes/identidad confirmados y acredita una publicación autoritativa posterior.
  Sus helpers `AssertOpenComponentBalances` y `AssertClosedComponentBalances` verifican en el nivel
  propietario staging, backups, candidatos, callbacks, handlers, suscripción del adaptador,
  consumers, raster owners, leases y timer del fixture.
- Nivel conjunto: `JointScanComponentAcceptanceTests.SelectionSupersessionAndFailedCallbacksStayInsideTheirBranch`
  mantiene la garantía de aislamiento entre deliveries A/B: un observador fallido de una rama no
  impide que la otra continúe. `AssertComponentBalances` se limita deliberadamente a balances
  públicos/conjuntos: consumers y leases B, owners observables del fixture, suscripción de sesión A,
  callbacks publicados B, dominio compartido, selección pendiente y timer propiedad del host.

Los contadores privados no se duplican ni se exponen en la aceptación conjunta: cada prueba de nivel
los comprueba donde el recurso se crea y posee. La combinación de los niveles interno, WPF y conjunto
es la garantía de aceptación; ninguna API ni seam diagnóstico adicional forma parte de ella.

## Riesgos

- `WpfUiDispatcher` y parte del wiring lifecycle están duplicados, pero extraerlos ahora aumentaría
  fronteras sin equivalencia suficiente.
- La demo es sintética y no sustituye una prueba con hardware o SDK propietario.

## Cuestiones pendientes

- Decidir una extracción WPF común solo cuando exista equivalencia demostrada y tercer consumidor.
- NuGet, C-Scan e infraestructura Scan2D común continúan fuera de alcance.
