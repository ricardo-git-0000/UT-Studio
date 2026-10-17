# Matriz de aceptación de la Fase 3 de procesamiento UT

Estado: ejecutable. Esta matriz es la trazabilidad normativa requisito → prueba para el cierre de P1. Los nombres se refieren a métodos MSTest; una fila parametrizada constituye evidencia directa para cada valor declarado.

| # | Escenario | Evidencia directa |
|---:|---|---|
| 1 | Commit RF anterior a procesamiento | `Scenarios01_02_08_CommitPrecedesSampleFreeProcessingAndThenVisualBranches` |
| 2 | Señal sin muestras | `SignalContractIsSampleFreeAndSchedulerCapacityIsBounded`; `Scenarios01_02_08_CommitPrecedesSampleFreeProcessingAndThenVisualBranches` |
| 3 | Flujo productivo completo | `CommittedSignalReadsEvaluatesDeliversAndBalancesLease` |
| 4 | Señal perdida | `FinalRevisionRecoversLostAndDuplicateSignalsWithoutDoubleEvaluation` |
| 5 | Señal duplicada | `FinalRevisionRecoversLostAndDuplicateSignalsWithoutDoubleEvaluation` |
| 6 | Señales desordenadas | `Scenario06_07_15_OutOfOrderLateAndCoalescedSignalsPreserveRevisionOrder` |
| 7 | Señal tardía | `Scenario06_07_15_OutOfOrderLateAndCoalescedSignalsPreserveRevisionOrder` |
| 8 | Carrera commit/enqueue | `Scenarios01_02_08_CommitPrecedesSampleFreeProcessingAndThenVisualBranches` |
| 9 | Fallo de procesamiento no revierte RF | `Scenarios09_10_17_ProcessingFailureNeverUndoesCommitOrBlocksVisuals` |
| 10 | A/B independientes del fallo | `Scenarios09_10_17_ProcessingFailureNeverUndoesCommitOrBlocksVisuals` |
| 11 | Rechazo tipado preadmisión | `Scenarios11_12_16_66_AdmissionAndSaturationAreTypedBoundedAndPreserveAcceptedRun` |
| 12 | Admitido preservado tras Stop/saturación | `Scenarios11_12_16_66_AdmissionAndSaturationAreTypedBoundedAndPreserveAcceptedRun` |
| 13 | Store más rápido | `Scenarios13_14_15_16_55_67_69_DeterministicRealOrchestratorLoadBalances` |
| 14 | Worker más rápido | `Scenarios13_14_15_16_55_67_69_DeterministicRealOrchestratorLoadBalances` |
| 15 | Revisiones coalescidas | `Scenario06_07_15_OutOfOrderLateAndCoalescedSignalsPreserveRevisionOrder` |
| 16 | Capacidad acotada | `Scenarios11_12_16_66_AdmissionAndSaturationAreTypedBoundedAndPreserveAcceptedRun` |
| 17 | Provider suspendido no bloquea sesión | `Scenarios09_10_17_ProcessingFailureNeverUndoesCommitOrBlocksVisuals` |
| 18 | Reactivación explícita | `Scenario18_22_NotAvailableIsNonterminalAndProviderProgressReactivatesItOnce` |
| 19 | Reactivación perdida recuperada al final | `Scenario19_LostProviderProgressIsRecoveredByFinalReconciliationPulse` |
| 20 | Sin polling, sleeps o cola por traza | `SignalContractIsSampleFreeAndSchedulerCapacityIsBounded` y revisión estructural del orquestador |
| 21 | Available | `CommittedSignalReadsEvaluatesDeliversAndBalancesLease` |
| 22 | NotAvailable | `Scenario18_22_NotAvailableIsNonterminalAndProviderProgressReactivatesItOnce` |
| 23 | Gap | `Scenarios23_24_25_26_27_ProviderTerminalStatesFailExplicitlyAndBalance` (`Gap`) |
| 24 | OutOfRange | misma prueba (`OutOfRange`) |
| 25 | Cancelled | misma prueba (`Cancelled`) |
| 26 | Closed | misma prueba (`Closed`) |
| 27 | Faulted | misma prueba (`Faulted`) |
| 28 | Excepción del catálogo | `Scenarios28_29_CatalogAndProviderExceptionsFailDrainWithoutLeakingCapacity` |
| 29 | Excepción del provider | `Scenarios28_29_CatalogAndProviderExceptionsFailDrainWithoutLeakingCapacity` |
| 30 | Revisión/descriptor incompatible | `Scenario30_MismatchedProviderRevisionIsTerminalFailureAndLeaseIsReleasedOnce` |
| 31 | Run abierta no completa | `Scenario31_32_33_OpenEmptyRunOnlyDrainsAfterFinalRevision` |
| 32 | Sellado hasta FinalRevision | misma prueba |
| 33 | Run vacía | misma prueba |
| 34 | Solo gaps | `Scenario34_35_GapOnlyAndSparseIndicesNeverCreateSyntheticTrace` |
| 35 | Índices dispersos | misma prueba |
| 36 | Plan fijado al admitir | `Scenario36_37_38_42_PlanEpochsArePinnedAndDiagnosedPerRun` |
| 37 | Trabajo posterior usa nuevo plan | misma prueba |
| 38 | Batch no mezcla planes | misma prueba |
| 39 | Cambio con provider suspendido | `Scenario39_PlanChangeWhileProviderIsSuspendedPinsOldAndUsesNewPlanNext` |
| 40 | Cambio durante evaluación | `Scenarios40_41_PlanCannotChangeAdmittedWorkAndNoTransitionGapIsInvented` |
| 41 | Hueco de transición diagnosticado | misma prueba |
| 42 | Plan/firma/revisión por run | `Scenarios42_67_68_69_71_MultiRunDiagnosticsAreSeparatedAndFailureIsIsolated` |
| 43 | Lease exactamente una vez | `CommittedSignalReadsEvaluatesDeliversAndBalancesLease` |
| 44 | Cancelación pre-lease | `Scenario44_DisposeBeforeProviderReadAcquiresNoLease` |
| 45 | Cancelación post-lease | `Scenarios45_60_DisposeOrStopDuringEvaluationWaitsForLeaseAndScratch` (`exceptionalDispose=true`) |
| 46 | Cancelación durante evaluación | `Scenarios45_60_DisposeOrStopDuringEvaluationWaitsForLeaseAndScratch` (`exceptionalDispose=true`) |
| 47 | Excepción durante evaluación | `Scenario47_EvaluationExceptionIsTerminalAndReleasesLeaseAndScratch` |
| 48 | Sink acepta | `Scenarios48_49_SinkOutcomesAreExplicitAndLosslessBalanced` (`Accepted`) |
| 49 | Sink rechaza | misma prueba (`Rejected`) |
| 50 | Sink lanza | `Scenarios50_51_ThrowingAndReentrantSinkAreOwnedAndDiagnosed` |
| 51 | Sink reentra | misma prueba |
| 52 | Callback suspendido durante Stop | `Scenarios52_61_StopWaitsForSuspendedCallbackAndReleasesLeaseAfterward` |
| 53 | Error primario y cleanup secundario | `Scenario53_PrimarySinkErrorAndSecondaryLeaseCleanupErrorRemainBalanced`; `PrimaryAndCleanupErrorsAreComposedAndFlattenedInOrder` |
| 54 | Batch no retenido | `Scenarios54_63_BorrowedProductSinkRetainsNothingAndDisposeDuringRunningBalances` |
| 55 | Recursos a cero | `Scenarios13_14_15_16_55_67_69_DeterministicRealOrchestratorLoadBalances` |
| 56 | Stop durante commit | `StopWaitsForAdmittedCommitThenSealsAndReleasesFrameOnce` |
| 57 | Stop durante reconcile | `Scenarios57_58_59_StopDuringCatalogOrProviderReadWaitsAndDrains` |
| 58 | Stop durante catálogo | misma prueba (`blockCatalog=true`) |
| 59 | Stop durante lectura | misma prueba (`blockCatalog=false`) |
| 60 | Stop durante evaluación | `Scenarios45_60_DisposeOrStopDuringEvaluationWaitsForLeaseAndScratch` (`exceptionalDispose=false`) |
| 61 | Stop durante callback | `Scenarios52_61_StopWaitsForSuspendedCallbackAndReleasesLeaseAfterward` |
| 62 | Dispose antes de Start | `Scenarios62_64_65_73_ConcurrentDisposeIsSharedAndClosedOnlyAfterCleanup` |
| 63 | Dispose durante Running | `Scenarios54_63_BorrowedProductSinkRetainsNothingAndDisposeDuringRunningBalances` |
| 64 | Stop/Dispose repetidos | `Scenarios62_64_65_73_ConcurrentDisposeIsSharedAndClosedOnlyAfterCleanup` |
| 65 | Stop/Dispose concurrentes | misma prueba |
| 66 | Rechazo durante cierre | `Scenarios11_12_16_66_AdmissionAndSaturationAreTypedBoundedAndPreserveAcceptedRun` |
| 67 | Segunda run | `Scenarios13_14_15_16_55_67_69_DeterministicRealOrchestratorLoadBalances` |
| 68 | Dos runs coexistentes | `Scenarios42_67_68_69_71_MultiRunDiagnosticsAreSeparatedAndFailureIsIsolated` |
| 69 | Watermarks distintos | misma prueba |
| 70 | Scratch compartido | `Scenario70_TwoOrchestratorsShareOneBoundedScratchPoolAndBalanceIt` |
| 71 | Fallo aislado por run | `Scenarios42_67_68_69_71_MultiRunDiagnosticsAreSeparatedAndFailureIsolated` |
| 72 | Cleanup intenta barreras | `ProcessingCleanupAttemptsDrainAfterFinalReconciliationFails` y pruebas de `ShutdownRobustnessTests` |
| 73 | Closed tras saldo cero | `Scenarios62_64_65_73_ConcurrentDisposeIsSharedAndClosedOnlyAfterCleanup` |

La contabilidad terminal por cada combinación run/plan es:

`ConfirmedEligible = EvaluatedAndDelivered + ExplicitlyRejectedBeforeAdmission + TerminallyFailed + ExplicitlyUnprocessedByPlanTransition`.

`Drained` solo se publica con revisión final reconciliada, trabajo admitido igual a terminal y workers, callbacks y leases activos a cero. Las ausencias de catálogo y los gaps no se convierten en identidades elegibles.
