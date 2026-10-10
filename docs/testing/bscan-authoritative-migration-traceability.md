# Trazabilidad de la migración autoritativa B-Scan

Fecha: 2026-10-10

## Alcance

Inventario individual de los 150 métodos cuyo nombre descubierto en `HEAD` ya no aparece en el árbol migrado. La clasificación se realiza contra el comportamiento confirmado: B-Scan recibe notificaciones sin muestras, reconcilia por revisión y proyecta páginas autoritativas mediante `IInspectionRangeProvider` e `IInspectionStreamStateProvider`.

- **Migrado**: el comportamiento sigue vigente y se cubre en las suites actuales indicadas.
- **Obsoleto**: verificaba exclusivamente el fan-out, ring, copia RF, pinning o índice derivado eliminados; no se porta.
- No quedan comportamientos vigentes clasificados como pendientes.

## Balance por método fuente

| Estado | Métodos |
|---|---:|
| Migrado | 121 |
| Obsoleto | 29 |
| Total | 150 |

La auditoría anterior contó 141 métodos porque se limitó a los archivos eliminados. El diff real de `dotnet test --list-tests` incluye además siete métodos de `ApplicationSessionIntegrityTests` y dos métodos WPF de archivos modificados.

## Balance por caso descubierto

Comparación exacta de conjuntos, conservando literalmente cada línea listada por `dotnet test --list-tests`:

«Total desaparecidos» es la diferencia de conjuntos por nombre, no la resta de cardinalidades: la suite actual contiene pruebas nuevas cuyos nombres no existían en `HEAD`.

| Suite | Casos en `HEAD` | Casos actuales | Desaparecidos migrados | Desaparecidos obsoletos | Total desaparecidos |
|---|---:|---:|---:|---:|---:|
| Core | 663 | 541 | 139 | 33 | **172** |
| WPF | 99 | 96 | 5 | 0 | **5** |
| Total | 762 | 637 | 144 | 33 | **177** |

Por tanto:

- Core: **172 = 139 migrados + 33 obsoletos**.
- WPF: **5 = 5 migrados + 0 obsoletos**.
- Los 145 métodos Core producen 172 casos descubiertos: la diferencia de 27 corresponde a expansión de `DataRow`.
- Los cinco métodos WPF producen cinco casos descubiertos; no hay expansión parametrizada entre los desaparecidos WPF.

Para todo método no parametrizado, el valor de «Método retirado» de la matriz es también el nombre completo listado. Las ocho excepciones parametrizadas se enumeran literalmente a continuación; no se cuenta el método contenedor como un noveno caso independiente.

| Método fuente | Casos descubiertos exactos en `HEAD` | Casos | Estado |
|---|---|---:|---|
| `AcceptFailureDisablesAndClosesOnlyFailingBranches` | `AcceptFailureDisablesAndClosesOnlyFailingBranches ("A")`<br>`AcceptFailureDisablesAndClosesOnlyFailingBranches ("B")`<br>`AcceptFailureDisablesAndClosesOnlyFailingBranches ("Both")` | 3 | Obsoleto |
| `ApplicationSessionWithFailingFanOutReleasesFramesAndKeepsAcquisitionRunning` | `ApplicationSessionWithFailingFanOutReleasesFramesAndKeepsAcquisitionRunning ("A")`<br>`ApplicationSessionWithFailingFanOutReleasesFramesAndKeepsAcquisitionRunning ("B")`<br>`ApplicationSessionWithFailingFanOutReleasesFramesAndKeepsAcquisitionRunning ("Both")` | 3 | Obsoleto |
| `BrokenBorrowedSinkDoesNotStopAcquisitionOrLeakFrames` | `BrokenBorrowedSinkDoesNotStopAcquisitionOrLeakFrames ("Open")`<br>`BrokenBorrowedSinkDoesNotStopAcquisitionOrLeakFrames ("Accept")`<br>`BrokenBorrowedSinkDoesNotStopAcquisitionOrLeakFrames ("Close")` | 3 | Migrado |
| `BScanCloseAttemptsEveryStageWhenOneStageFails` | `BScanCloseAttemptsEveryStageWhenOneStageFails ("subscription")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("time-viewport-events")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("selection-events")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("selection-cancel")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("raster-cancel")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("raster-operations")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("preference-writes")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("ui-state")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("time-viewport-consumer")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("visible-region")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("raster-consumer")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("ui-barrier")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("ui-lifetime")`<br>`BScanCloseAttemptsEveryStageWhenOneStageFails ("preference-cancellation")` | 14 | Migrado |
| `CoalescedRevisionsRemainIncrementalWhileTheirBaseIsResident` | `CoalescedRevisionsRemainIncrementalWhileTheirBaseIsResident (1)`<br>`CoalescedRevisionsRemainIncrementalWhileTheirBaseIsResident (8)`<br>`CoalescedRevisionsRemainIncrementalWhileTheirBaseIsResident (33)`<br>`CoalescedRevisionsRemainIncrementalWhileTheirBaseIsResident (100)` | 4 | Migrado |
| `InterpolatingModesAreFullOnlyAndKeepScratchAndOwnersBalanced` | `InterpolatingModesAreFullOnlyAndKeepScratchAndOwnersBalanced (Bilinear)`<br>`InterpolatingModesAreFullOnlyAndKeepScratchAndOwnersBalanced (Bicubic)`<br>`InterpolatingModesAreFullOnlyAndKeepScratchAndOwnersBalanced (Lanczos)` | 3 | Migrado |
| `InterpolatingModesAtMaximumRasterStayWithinNeutralBudget` | `InterpolatingModesAtMaximumRasterStayWithinNeutralBudget (Bilinear)`<br>`InterpolatingModesAtMaximumRasterStayWithinNeutralBudget (Bicubic)`<br>`InterpolatingModesAtMaximumRasterStayWithinNeutralBudget (Lanczos)` | 3 | Migrado |
| `ModeChangeMakesActiveLanczosStaleAndPublishesOnlyTheNewMode` | `ModeChangeMakesActiveLanczosStaleAndPublishesOnlyTheNewMode (Bicubic)`<br>`ModeChangeMakesActiveLanczosStaleAndPublishesOnlyTheNewMode (None)` | 2 | Migrado |

## Matriz individual

| # | Método retirado | Origen | Estado | Evidencia actual / justificación |
|---:|---|---|---|---|
| 1 | `StopWaitsForBScanCloseBarrierBeforeAllowingNextRun` | `Core/Application/BScanSessionLifecycleTests.cs` | Migrado | ApplicationSessionInspectionStoreTests + ApplicationSessionIntegrityTests |
| 2 | `ApplicationSessionWithFailingFanOutReleasesFramesAndKeepsAcquisitionRunning` | `Core/Application/ConventionalFrameSinkFanOutTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 3 | `InvokesAThenBOnceWithExactPerFrameDataAndNoCopyingLayer` | `Core/Application/ConventionalFrameSinkFanOutTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 4 | `AcceptFailureDisablesAndClosesOnlyFailingBranches` | `Core/Application/ConventionalFrameSinkFanOutTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 5 | `OpenAndCloseFailuresAreIsolatedAndMultipleBranchErrorsAreRetained` | `Core/Application/ConventionalFrameSinkFanOutTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 6 | `ClosedFanOutCanOpenAnotherRunAndResetsBranchState` | `Core/Application/ConventionalFrameSinkFanOutTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 7 | `CloseMayOverlapAdmittedAcceptAndClosesEachBranchExactlyOnce` | `Core/Application/ConventionalFrameSinkFanOutTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 8 | `SessionPublishesIndependentDataAndReleasesEveryFrameWithSlowObserver` | `Core/Application/SessionVisualDeliveryTests.cs` | Migrado | ApplicationSessionInspectionStoreTests + ApplicationSessionIntegrityTests |
| 9 | `BrokenBorrowedSinkDoesNotStopAcquisitionOrLeakFrames` | `Core/Application/SessionVisualDeliveryTests.cs` | Migrado | ApplicationSessionInspectionStoreTests + ApplicationSessionIntegrityTests |
| 10 | `ProjectionFailureIsDiagnosedWithoutStoppingSourceOrAuthority` | `Core/Application/SessionVisualDeliveryTests.cs` | Migrado | ApplicationSessionInspectionStoreTests + ApplicationSessionIntegrityTests |
| 11 | `Synchronous_raster_source_runs_off_dispatcher_and_does_not_block_ui` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 12 | `Resampling_command_exposes_five_modes_and_latest_request_wins_without_relabelling_visible_raster` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 13 | `SelectionProjectsExactSamplesAndLatestSelectionWins` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 14 | `FailedSelectionKeepsPreviousAndRunChangeInvalidatesIt` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 15 | `Unresolvable_latest_selection_cancels_suspended_resolution_and_preserves_visible_selection` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 16 | `BScanUsesLocalVersionAndSharedViewportAndRemovesOnlyItsConsumer` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 17 | `AScanSelectedKeepsSignalDuringLiveAndReturnToLiveIsImmediate` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 18 | `StoppedRunKeepsFrozenLiveSignalAndAllowsSelectionAndReturnToLive` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 19 | `PresentationRemainsNeutralAndViewModelsDoNotReferenceEachOther` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 20 | `ReversedMetadataCallbacksCannotRestoreAnOlderRun` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 21 | `CoordinatorCloseWaitsForEverySupersededResolutionAndPublishesNothingLate` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 22 | `RasterPublicationCopiesOwnerPairsRevisionAndCommandsStayUiAffine` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 23 | `Initial_live_raster_is_not_starved_by_newer_autofit_metadata` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 24 | `Live_autofit_uses_fixed_ring_width_then_slides_after_wrap` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 25 | `Provisional_one_pixel_raster_does_not_starve_sized_live_refresh` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 26 | `PresentedRegionContractContainsMetadataOnly` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 27 | `BScanCloseAttemptsEveryStageWhenOneStageFails` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 28 | `BScanClosePreservesPrimaryAndOrderedSecondaryFailures` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 29 | `RealCleanupDependenciesFailAfterBalancingAndLaterStagesStillRun` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 30 | `BScanCloseFromNonUiConcurrentCallersSharesTaskAndRunsStagesOnce` | `Core/Presentation/BScanPresentationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests |
| 31 | `AcceptProviderAndIndexPreserveExactSamplesAndLogicalSlice` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 32 | `ProviderDistinguishesMissingEvictedCancelledAndOtherRun` | `Core/Visualization/BScanIngressTests.cs` | Migrado | BScanRangeProjectionTests (reconciliación, estados, revisión y cierre) |
| 33 | `ConfigurationAndPostCloseRejectionsRemainSeparate` | `Core/Visualization/BScanIngressTests.cs` | Migrado | BScanRangeProjectionTests (reconciliación, estados, revisión y cierre) |
| 34 | `CancellationDuringCopyReturnsOwnerAndLease` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 35 | `AtMostTwoOwnedCopiesExistAndCancellationDoesNotAccumulateOwners` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 36 | `ThrowingSnapshotDisposeStillReleasesCopyQuotaAndRevokesSamples` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 37 | `ConcurrentSnapshotDisposeDoesNotReleaseQuotaBeforeOwnerReturnFinishes` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 38 | `CancellationAfterCopyReturnsCandidateOwner` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 39 | `ConcurrentSelectionPinsSlotAndWrapResumesAfterCopy` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 40 | `WorkerSignalCoalescesAndOverwrittenWorkIsSuperseded` | `Core/Visualization/BScanIngressTests.cs` | Migrado | BScanRangeProjectionTests (reconciliación, estados, revisión y cierre) |
| 41 | `WorkerFailureDoesNotPreventCleanupAndCloseIsIdempotent` | `Core/Visualization/BScanIngressTests.cs` | Migrado | BScanRangeProjectionTests (reconciliación, estados, revisión y cierre) |
| 42 | `FailedBlockReturnFaultsSharedDrainAndIsNeverRetried` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 43 | `CloseCancelsProviderCopyAndWaitsForItsLease` | `Core/Visualization/BScanIngressTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 44 | `MinMaxBucketsHandlePartialBucketAndInt16Extremes` | `Core/Visualization/BScanIngressTests.cs` | Migrado | `BScanRangeProjectionTests.DirectRasterHandlesPartialTimeBucketsAndInt16Extremes` |
| 45 | `MetadataOriginIsValidatedAndAcceptDurationsAreBoundedAndDeterministic` | `Core/Visualization/BScanIngressTests.cs` | Migrado | BScanRangeProjectionTests (reconciliación, estados, revisión y cierre) |
| 46 | `OpeningAnotherRunResetsRunScopedDiagnostics` | `Core/Visualization/BScanIngressTests.cs` | Migrado | BScanRangeProjectionTests (reconciliación, estados, revisión y cierre) |
| 47 | `StopCancelsActiveLanczosAtNextRowAndDrainsEveryResource` | `Core/Visualization/BScanLanczosResponsivenessTests.cs` | Migrado | `BScanRangeProjectionTests.StopDuringActiveLanczosReturnsImmediatelyAndDrainsAfterNextRow` |
| 48 | `ConcurrentDisposeCancelsActiveLanczosAndSharesTheCloseTask` | `Core/Visualization/BScanLanczosResponsivenessTests.cs` | Migrado | `BScanRangeProjectionTests.DisposeDuringActiveLanczosCancelsAndBalancesAtNextRow` |
| 49 | `SupersededLanczosNeverTransfersAndOnlyLatestCompatibleSignaturePublishes` | `Core/Visualization/BScanLanczosResponsivenessTests.cs` | Migrado | `BScanRangeProjectionTests.ModeChangeSupersedesActiveLanczosAndOnlyNewModeCanCommit` |
| 50 | `ModeChangeMakesActiveLanczosStaleAndPublishesOnlyTheNewMode` | `Core/Visualization/BScanLanczosResponsivenessTests.cs` | Migrado | `BScanRangeProjectionTests.ModeChangeSupersedesActiveLanczosAndOnlyNewModeCanCommit` |
| 51 | `OptionsValidateBounds` | `Core/Visualization/BScanTraceStoreTests.cs` | Migrado | `BScanRangeProjectionTests.ProjectionOptionsRejectEveryOutOfContractBound` |
| 52 | `VisualBudgetIsAppliedBeforeRentingRfBlocks` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 53 | `DefaultBudgetReservesExpectedEffectiveMemoryForSmallAndMaximumTraces` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 54 | `OversizedRentAndIntermediateFailureRollBackTransactionally` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 55 | `SuccessfulOversizedRentChargesPhysicalLengthAndPartialLastBlock` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 56 | `CloseAttemptsEveryBlockWhenPoolReturnFails` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 57 | `PinRejectsNextSlotWithoutWaitingAndGenerationPreventsAba` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 58 | `CloseWaitsForOutstandingLeaseBeforeReturningBlocks` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 59 | `CloseWaitsForAdmittedWriterAndDoesNotCommitIt` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 60 | `Visual_state_is_not_current_while_slot_is_writing_or_waiting_for_index` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 61 | `Visual_capture_keeps_next_wrap_slots_writable` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 62 | `Second_capture_rent_failure_rolls_back_transactionally_one_hundred_times` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 63 | `Capture_failure_matrix_cancels_cleans_and_recovers_twenty_times` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 64 | `Capture_preserves_primary_and_attempts_all_cleanup_after_secondary_failures` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 65 | `Concurrent_close_waits_for_capture_rollback_and_then_completes` | `Core/Visualization/BScanTraceStoreTests.cs` | Obsoleto | Eliminado con el ingreso RF, ring/copia/pin visual o fan-out legacy; el store autoritativo asume ese contrato. |
| 66 | `AuditRingIsBoundedOrderedAndReportsOverflow` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 67 | `BipolarMappingIsMonotonicAndReservesZeroForNoData` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 68 | `SpatialViewportPreservesPrecisionBeyondDoubleIntegerRangeAndClamps` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 69 | `Bilinear_upsamples_visible_rf_samples_along_time_axis_instead_of_repeating_rows` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 70 | `FullRasterUsesNoDataGapsStableTieAndSurvivesRingWrap` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 71 | `RevisionMismatchForcesFullAndMatchingBaseProducesIncremental` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 72 | `NoneKeepsExactBytesAndNearestUsesMaskAndFullRecovery` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 73 | `LanczosInteriorViewportUsesHorizontalHaloAndSeesGapOutsideVisibleRange` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 74 | `CandidateIsStaleWhenModeChangesBeforeTransactionalCommit` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 75 | `UnknownAlgorithmVersionIsRejectedBeforeDelivery` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 76 | `InterpolatingModesAreFullOnlyAndKeepScratchAndOwnersBalanced` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 77 | `InterpolatingModesAtMaximumRasterStayWithinNeutralBudget` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 78 | `ConcurrentLanczosBuildsPreserveTheOtherConsumersProportionalShare` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 79 | `ConsumerByteCeilingPreservesTheOtherConsumersProportionalShare` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 80 | `OwnerQuotaIsBoundedAndDisposeIsIdempotent` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 81 | `StopBlocksAdmissionAndDrainWaitsForTransferredOwnerWithoutForcingRelease` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 82 | `ObservablePublishesMetadataOnlyAndFailingObserverDoesNotStopPeer` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 83 | `DisposeBeforeCommitRejectsCandidateAndAuditOrdersClosure` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 84 | `TwoLaterRequestsSupersedeCandidateAndOnlyLatestCanCommit` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 85 | `LiveAppendDoesNotInvalidateCapturedRasterCandidate` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 86 | `RemovingConsumerWithPendingBuildRejectsWithoutCallbackOrRetainedState` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 87 | `NewRunBeforeCommitRejectsOldCandidateAndOnlyNewRunCanCommit` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 88 | `ReentrantCallbackAdmitsLaterModeAfterCurrentCallbackCompletes` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 89 | `FaultAfterSupersessionDoesNotReplaceCurrentDiagnosticsOrRecovery` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 90 | `Post_capture_build_failure_drains_every_resource_twenty_times` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 91 | `TwoConsumersReserveConcurrentRoundedRentalsWithinIndependentShares` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 92 | `ConcurrentRoundedRentalsReserveBeforeRentAndNeverExceedBudget` | `Core/Visualization/BScanVisualDeliveryTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (raster, NoData, revisiones, cuotas y modos) |
| 93 | `TwoConcurrentCallersAreAdmittedLinearlyWhenExactCapacityFits` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 94 | `CancellationAfterAcquireRollsBackTheReservationAndAllowsRetry` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 95 | `FailureBetweenAcquireAndTransferCleansUpAndAllowsRetry` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 96 | `ExactLimitIsAdmittedAndOneByteOverIsRejectedBeforeAllocation` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 97 | `DimensionOverflowDoesNotAlterTheLedger` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 98 | `DifferentBucketsRemainChargedWhileRetainedAndAreReusedWithoutGrowth` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 99 | `BucketKeyChurnEvictsRetainedEntriesAndDoesNotCausePermanentExhaustion` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 100 | `CloseDropsRetainedBuffersButWaitsForBorrowedOwner` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 101 | `ConcurrentFailuresNeverMakeCountersNegativeOrIncreaseMaximumPastLimit` | `Core/Visualization/BScanVisualMemoryTests.cs` | Migrado | BScanRangeProjectionTests (ledger físico, capacidad, overflow y balances) |
| 102 | `CoalescedRevisionsRemainIncrementalWhileTheirBaseIsResident` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 103 | `FastAndSlowConsumersAdvanceFromIndependentResidentBases` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 104 | `FirstPublicationHasExactInitialReason` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 105 | `IncompatibleBaseHasExactReasonAndNeverProducesIncrementalRegion` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 106 | `SnapshotVersionGapAloneDoesNotForceFullRefresh` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 107 | `IncompatibleWrapHasExactReason` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 108 | `SpatialViewportChangeHasExactReason` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 109 | `TemporalViewportChangeHasExactReason` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 110 | `FrozenRunRemainsRenderableAndSelectableUntilRetired` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 111 | `RasterDimensionChangeHasExactReason` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 112 | `RenderConfigurationChangeHasExactReason` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 113 | `PartiallyEvictedDirtyRangeHasExactReasonAndNoGenerationMix` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 114 | `CompletelyEvictedDirtyRangeForcesFullAndLeavesNoOwners` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 115 | `Live_window_is_reconciled_with_captured_ring_after_queued_wrap` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 116 | `Continuous_traces_expand_without_no_data_when_raster_is_wider_than_signal_count` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 117 | `Full_raster_releases_oldest_slot_before_finishing_remaining_columns` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 118 | `ConstructionErrorForcesExactRecoveryOnNextCandidate` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 119 | `RetainedCandidateLeaseDelaysIndexingUntilCandidateCompletes` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 120 | `Bootstrap_candidate_releases_lease_before_pending_index_completes` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 121 | `Wide_live_window_candidate_releases_lease_before_pending_index_completes` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 122 | `NewRunForcesExactRunChangedReason` | `Core/Visualization/BScanVisualProtocolTests.cs` | Migrado | BScanRangeProjectionTests + BScanProductiveIntegrationTests (rangos y generaciones) |
| 123 | `Wide_live_windows_use_responsive_coalesced_publication_rate` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 124 | `FirstPublicationIsImmediateWithManualClock` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 125 | `RequestsBeforeBoundaryAreCoalescedAndExactBoundaryPublishesLatest` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 126 | `BurstAcrossSeveralPeriodsNeverCatchesUpAndKeepsLatestRequest` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 127 | `IndexedColumnWithoutExternalRequestReusesLatestRequest` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 128 | `PendingTimerIsCancelledAndDisposedDuringClose` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 129 | `UnsubscribeBeforeCallbackPreventsCallbackEntry` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 130 | `UnsubscribeDuringCallbackWaitsForStartedCallbackAndPreventsAnother` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 131 | `DisposeWaitsForStartedCallbackAndNoCallbackRemainsAfterCompletion` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 132 | `SlowAndFailingObserversDoNotBlockHealthyObserver` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 133 | `ReentrantObserverCanUnsubscribeItself` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 134 | `TwoConsumersReceiveMetadataOnlyAndOwnIndependentRasterRegions` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 135 | `SupersededMetadataCandidateIsNotPublishedAndLatestRequestWins` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 136 | `CancellationAfterQuotaAcquisitionReturnsPermitAndOwner` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 137 | `CloseCancelsThirdRequestWaitingForQuota` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 138 | `TimerFailureIsDiagnosedAndCloseLeavesNoTimerOrCallback` | `Core/Visualization/BScanVisualPublicationTests.cs` | Migrado | BScanProductiveIntegrationTests + CompositionTests (latest-only, callbacks y drain) |
| 139 | `Delivery_viewmodel_adapter_applies_full_and_unsubscribes_on_close` | `Wpf/BScanAdapterIntegrationTests.cs` | Migrado | BScanProductiveIntegrationTests |
| 140 | `Adapter_commits_accumulated_1_8_33_100_column_regions_without_replacing_bitmap` | `Wpf/BScanAdapterIntegrationTests.cs` | Migrado | BScanProductiveIntegrationTests |
| 141 | `Requested_column_is_visible_while_exact_trace_resolution_is_pending` | `Wpf/BScanAdapterIntegrationTests.cs` | Migrado | BScanProductiveIntegrationTests |
| 142 | `InvalidInitialAcquisitionIndexCanBeFollowedByZero` | `Core/Application/ApplicationSessionIntegrityTests.cs` | Migrado | `InvalidInitialIndexCanBeFollowedByZeroAndOnlyCommitIsNotified` |
| 143 | `NewRunResetsWatermarksAndIntegrityCountersOnSameSession` | `Core/Application/ApplicationSessionIntegrityTests.cs` | Migrado | `NewRunResetsWatermarksAndIntegrityCounters` |
| 144 | `SessionForwardsExactPerFrameIndexCoordinateAndBorrowedSamples` | `Core/Application/ApplicationSessionIntegrityTests.cs` | Migrado | `CommittedFrameIsAuthoritativeBeforeVisualProjectionAndSurvivesOwnerReturn` |
| 145 | `ContiguousGapsAndLimitsAreDeliveredWithExactMissingCount` | `Core/Application/ApplicationSessionIntegrityTests.cs` | Migrado | `SequenceGapsPreserveAcquisitionIndicesWithoutSyntheticNotifications`; límite `ulong` en `SingleColumnEndingAtUlongMaximumDoesNotOverflow` |
| 146 | `RejectionsUseIndependentCountersAndNextValidFrameIsDelivered` | `Core/Application/ApplicationSessionIntegrityTests.cs` | Migrado | `RejectionsUseIndependentCountersAndNextValidFrameIsCommitted` |
| 147 | `InvalidFirstSequenceCanBeFollowedByZeroAndGapIsNotCountedTwiceByDuplicate` | `Core/Application/ApplicationSessionIntegrityTests.cs` | Migrado | `SequenceGapsPreserveAcquisitionIndicesWithoutSyntheticNotifications`; duplicados en `RejectionsUseIndependentCountersAndNextValidFrameIsCommitted` |
| 148 | `MaximumSequenceRejectsDuplicateAndAnyFollowingLowerValueWithoutOverflow` | `Core/Application/ApplicationSessionIntegrityTests.cs` | Migrado | rechazo de duplicados en `RejectionsUseIndependentCountersAndNextValidFrameIsCommitted`; frontera `ulong` en `SingleColumnEndingAtUlongMaximumDoesNotOverflow` |
| 149 | `Real_delivery_viewmodel_adapter_control_commits_full_and_incrementals` | `Wpf/BScanProductiveIntegrationTests.cs` | Migrado | `Full_and_incremental_updates_reuse_bitmap_and_preserve_unaffected_pixels`; `Full_live_ring_scrolls_one_column_without_rebuilding_the_complete_raster` |
| 150 | `Failed_block_return_keeps_productive_cleanup_unconfirmed_and_shell_visible` | `Wpf/CompositionTests.cs` | Migrado | `UnconfirmedCleanupSkipsHostDisposalAndPreservesStructuredFailures` |

## Criterio de cierre

La matriz se valida junto con las suites Core y WPF completas. Una fila “Migrado” no conserva necesariamente la antigua estructura de fixture: conserva la obligación observable bajo la arquitectura autoritativa. Las filas “Obsoleto” no representan cobertura perdida porque sus objetos de prueba fueron retirados deliberadamente y no existe ya una ruta productiva equivalente.

