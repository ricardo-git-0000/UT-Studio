# Trazabilidad canónica de Fase 5

Estado vigente: 2026-10-18. Sustituye todos los inventarios anteriores. `NotRepresentableByContract` significa
verificación estructural y por invariantes alcanzables, nunca fallo inyectado.

| ID | Frontera | Clasificación | Propietario | Prueba/evidencia | Resultado | Repeticiones | Justificación contractual |
| ---: | --- | --- | --- | --- | --- | ---: | --- |
| 01 | Construcción interna del snapshot | NotRepresentableByContract | DataStore | inspección de `OpenSealedRunSnapshotCore`; `SnapshotOwnershipIsAdmittedOnceAndConcurrentDisposeReleasesExactlyOnce`; `RejectedAndCancelledSnapshotOpenNeverCreateOwnership`; `CloseWaitsForActiveSnapshotAndDoesNotForceIt`; `PreparedQueryBudgetIsAggregatedAcrossSnapshotsAndReleasedOnDispose` | Verde | 2 | RNG, materialización y constructor no son dependencias fallables del contrato; rollback estructural antes de publicar. |
| 02 | Encabezado | Ejecutable | scheduler | `SnapshotHeaderMismatchIsRejectedBeforeRegionalQueryPreparation` | Verde | 1 | Validación previa a consultas. |
| 03 | Construcción del índice sellado | CoveredBeforeSchedulerAdmission | DataStore | `SealedIndexPublicationIsAtomicAndDoesNotDoubleAccountOwnership` | Verde | 1 | Publicación única durante sellado. |
| 04 | Reserva del índice | CoveredBeforeSchedulerAdmission | DataStore | `SealedIndexBudgetIsReservedOnceAndRollsBackAtomically` | Verde | 1 | Capacidad tipada y rollback. |
| 05 | Consulta regional | Ejecutable | snapshot/scheduler | fallo de admisión + cleanup | Verde | 1 | Primario preservado. |
| 06 | Presupuesto de consultas | Ejecutable | snapshot | pruebas `PreparedQueries*` | Verde | 2 | Cuenta/bytes balanceados. |
| 07 | Primera página | Ejecutable | snapshot | `F07_FirstPageFailure` | Verde | 1 | Sin prefijo falso. |
| 08 | Página intermedia | Ejecutable | snapshot | `F08_IntermediatePageFailure` | Verde | 1 | Prefijo confirmado preservado. |
| 09 | Cursor | Ejecutable | snapshot | `F09_InvalidCursorFailure`; cursor extranjero | Verde | 2 | Ligado a snapshot/query. |
| 10 | Identidad/orden/stream/revisión | Ejecutable | scheduler | F10 A–C y revisión incompatible | Verde | 4 | Mismatch sin publicación. |
| 11 | Lectura exacta | Ejecutable | provider/scheduler | F11 y resultados tipados | Verde | 2 | Clasificación preevaluación. |
| 12 | Descriptor | Ejecutable | lease/scheduler | `DescriptorFailureTransfersOwnershipAndReleasesLeaseExactlyOnce` | Verde | 1 | Ownership exacto. |
| 13 | `ReadAsync` + lease | IndivisibleContractOperation | snapshot/scheduler | casos A–D | Verde | 103 | Available y lease se entregan juntos. |
| 14 | Copia RF | Ejecutable | muestras/scheduler | `SampleCopyFailureReturnsBufferAndLeaseExactlyOnce` | Verde | 1 | Primario y cleanup real. |
| 15 | Renta de buffer | NotRepresentableByContract | buffer pool | `HistoricalSampleBufferBudgetReservesRejectsAndBalancesExactly`; `HistoricalBufferInvariantBudgetsOnePerWorkerAndRejectsForeignAndDoubleReturn` | Verde | 2 | Pool encapsulado, sin seam; se verifica presupuesto por worker, rechazo previo y balance, incluido foreign/double-return. |
| 16 | Renta de scratch | Ejecutable | scratch/evaluador | contención real | Verde | 3 | Rechazo productivo. |
| 17 | Stop tras entrada exacta a `Evaluate` visto por scheduler | NotRepresentableByContract | scheduler/evaluador | `StopDuringRfCopyCancelsBeforeEvaluationAndBalancesTerminalResourcesAcrossOneHundredRuns`; inspección de token; `GateEvaluatorTests` | Verde | 100 | El evaluador recibe span tras la copia; no existe señal contractual de entrada. |
| 18 | Devolución de scratch | NotRepresentableByContract | scratch lease | `CancellationAtEveryCheckpointReturnsCompleteTerminalBatch`; `CancellationInsideLongPlateauIsObservedAndScratchIsReturned`; `ScratchLeaseRejectsForeignAndDoubleReturnWithoutCorruptingBalances` | Verde | 12 | Retorno encapsulado no fallable; ownership y devolución exacta se verifican en éxito, cancelación y rechazo de retornos inválidos. |
| 19 | Fallo interno de devolución de buffer | NotRepresentableByContract | buffer pool | `SampleFailureReturnsBufferWithoutInventingUnrepresentableCleanupFailure` | Verde | 101 | No se fabrica error secundario imposible. |
| 20 | Liberación del lease | Ejecutable | lease/scheduler | leases contractuales fallables | Verde | 102 | Liberación exacta. |
| 21 | Commit preadmisión | Ejecutable | store/scheduler | store contractual; pairwise 07–09 | Verde | 3 | `WasAccepted=false`. |
| 22 | Commit posadmisión | Ejecutable | store/scheduler | store contractual; pairwise 10–12/19–21 | Verde | 6 | Operación admitida termina. |
| 23 | Duplicate | Ejecutable | store/scheduler | intento repetido; pairwise 04–06 | Verde | 4 | Reutilización íntegra. |
| 24 | Conflict | Ejecutable | store/scheduler | resultado tipado | Verde | 2 | Terminal exacto. |
| 25 | CapacityExceeded | Ejecutable | store/scheduler | límites exactos | Verde | 2 | Sin reserva parcial. |
| 26 | Closed | Ejecutable | store/scheduler | resultado tipado | Verde | 1 | Sin commit. |
| 27 | Faulted | Ejecutable | store/scheduler | resultado tipado | Verde | 2 | Primario preservado. |
| 28 | CoverageMismatch | Ejecutable | store/scheduler | digest/membresía pública | Verde | 2 | Sin vista parcial. |
| 29 | Fallo interno al construir manifest | NotRepresentableByContract | scheduler | inspección de `GateReprocessingScheduler.Job.BuildCoverage`; `SparseRunCompletesStoreOnlyAndSecondAttemptReusesDuplicates`; `ActivateOnCompletePublishesExactSparseViewOnlyAtTerminalState`; `MultiStreamSparseRegionProcessesExactSetWithObservableComplexity` | Verde | 3 | Construcción local sin dependencia fallable; las rutas alcanzables verifican manifest completo, duplicados, región sparse multi-stream y ausencia de publicación parcial. |
| 30 | Publicación de cobertura | Ejecutable | store/scheduler | resultados públicos y pairwise | Verde | 6 | Publicación atómica. |
| 31 | Membresía | Ejecutable | store | digest/conjunto incorrecto y multistream | Verde | 3 | Mismatch productivo. |
| 32 | Activación | Ejecutable | store/scheduler | políticas y conflictos | Verde | 6 | Vista atómica. |
| 33 | Diagnostics fallable | Ejecutable | snapshot/scheduler | provider fallable | Verde | 100 | Cleanup continúa. |
| 34 | Cleanup de acumuladores | NotRepresentableByContract | scheduler | inspección de `GateReprocessingScheduler.CoverageAccumulator`; `CoverageAccumulatorOwnsOnlyLocalCryptographicStateAndNoExternalResources` | Verde | 1 | Solo estado local; no posee leases, buffers, scratch, callbacks, I/O ni código usuario; el cleanup externo continúa independientemente. |
| 35 | Fallo interno de `ReleaseLease` | NotRepresentableByContract | snapshot/store | inspección de `InMemoryInspectionDataStore.ReleaseLease` y `SealedRunSnapshot.Dispose`; `SnapshotOwnershipIsAdmittedOnceAndConcurrentDisposeReleasesExactlyOnce`; `CloseWaitsForActiveSnapshotAndDoesNotForceIt`; `PreparedQueryBudgetIsAggregatedAcrossSnapshotsAndReleasedOnDispose` | Verde | 3 | Solo contadores y señal interna; sin I/O, callback, código usuario ni dependencia fallable; Dispose normal, repetido y concurrente verifican sus efectos. |
| 36 | Primario + secundario | Ejecutable | scheduler | lease/snapshot contractuales fallables | Verde | 100 | Orden y cleanup no confirmado. |
| 37 | Primario + secundarios | Ejecutable | scheduler | lease/snapshot/diagnostics fallables | Verde | 100 | Aplanado estable sin buffer artificial. |

Totales vigentes: **37/37 fronteras adjudicadas**, **26 fronteras ejecutables**, **2 cubiertas antes del scheduler**,
**1 operación indivisible** y **8 `NotRepresentableByContract`**.

## Pairwise productivo

Las 21 filas comparan literalmente etapa terminal, fronteras provider/store/copia RF, lifecycle, política, vista,
batches nuevos/duplicados y `CleanupConfirmed`. La frontera representable se denomina exclusivamente
`StopDuringRfCopy`. Las ocho interacciones críticas mantienen aserciones nominales.

## Evidencia estructural

- `OpenSealedRunSnapshotCore` incrementa `_activeLeases` una vez, rodea toda construcción posterior con `try/catch`,
  revierte una vez antes de transferir ownership y solo publica la referencia completamente construida.
- Tras la transferencia, únicamente el Dispose idempotente del snapshot libera el lease. `ReleaseLease` no ejecuta I/O,
  callbacks, código usuario ni dependencias fallables. Si esto cambia, corresponderá añadir entonces una prueba específica.
- El scheduler pasa directamente `job.Cancellation.Token` a `GateEvaluator.Evaluate`, no lo sustituye ni captura otro;
  conserva `Cancelled` y no entrega al store un batch cancelado.
