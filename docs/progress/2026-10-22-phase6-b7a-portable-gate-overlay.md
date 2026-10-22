# Fase 6 B7A — estado portable de overlay e interacción de gates

## Objetivo

Integrar el artefacto exacto de Preview con evaluación geométrica, snapshot visual neutral e interacción portable,
sin activar WPF, Apply productivo ni la composición del Paso 8.

## Resultado

- `GatePlanEditorSession` conserva el `CompiledGatePlan` exacto producido por Preview en un workspace internal.
- El workspace distingue un checkpoint estable y un candidato. Solo existe un artefacto estable; el candidato y
  las referencias en vuelo están acotados por un runner con una evaluación activa y una solicitud pendiente
  sustituible.
- Promover geometría convierte el candidato en estable. Escape reinstala el checkpoint y el mismo artefacto
  estable sin recompilar. El draft y el plan compilado no se publican en el snapshot visual; la identidad Preview
  sí se transporta para que el consumidor pueda descartar resultados obsoletos.
- `GateOverlaySession` separa generación externa, nonce de captura y secuencia de evaluación. Los movimientos
  propios conservan generación y nonce; cambios externos cancelan captura y trabajo visual incompatible.
- La publicación es serial y reentrante. Los resultados tardíos se terminalizan, pero no pueden hacer retroceder
  draft, geometría o selección.
- El snapshot compuesto reúne señal, cursores, gates y transformación física versionada. Una geometría stale o
  incompatible no se combina con señal confirmada.
- El hit testing es bidimensional y neutral. La selección, foco lógico, pasos, límites, teclado y arbitraje
  cursor/gate quedan preparados sin bindings WPF.

## Decisiones confirmadas

- Capacidad latest-wins: una evaluación activa más una pendiente sustituible; no hay una tarea por movimiento.
- Los tokens de B7A cancelan solo lectura visual. No alcanzan procesamiento productivo.
- Tiempo se edita en segundos con paso `visibleSpan / 1000`; umbral normalizado usa paso `0.01` y rango `[0,1]`.
- `Enter` termina la captura solo cuando el último Preview estable está confirmado. No ejecuta Apply.
- `Escape` restaura únicamente si el checkpoint pertenece a la revisión autoritativa todavía vigente.
- `AScanViewState` expone un slot opcional para el agregado futuro, pero B7A no lo alimenta ni modifica la UI.

## Límites

- B7B conserva: DP visual única, bindings, render, mouse, foco WPF y Automation peers.
- Paso 8 conserva: Apply productivo, adaptador/guard real, DI, lifetimes y aceptación vertical.
- B7A no crea autoridad, ledger, epochs, reprocesado ni lifecycle headless alternativo.

## Archivos modificados

- `src/UTStudio.Presentation/GatePlanEditorSession.cs`
- `src/UTStudio.Presentation/AScanViewState.cs`
- `src/UTStudio.Presentation/AScanViewModel.cs`

## Archivos nuevos

- `src/UTStudio.Presentation/GatePreviewWorkspace.cs`
- `src/UTStudio.Presentation/GateOverlaySession.cs`
- `src/UTStudio.Visualization.Core/GateOverlayVisualState.cs`
- `tests/UTStudio.Core.Tests/Presentation/GateOverlaySessionTests.cs`
- `docs/progress/2026-10-22-phase6-b7a-portable-gate-overlay.md`

## Pruebas y validaciones

- Focalizadas B7A/editor/proyección/arquitectura: `27/27` correctas antes del endurecimiento final; la suite
  específica B7A final contiene `11/11` pruebas correctas.
- Core completa final con un worker: `1054/1054` correctas. Las dos ejecuciones paralelas inmediatamente
  anteriores fallaron cada una en una prueba de lifecycle ajena al diff: una observó `Running` en lugar de
  `Stopped` y otra `FinalReconciling` en lugar de `Faulted`. No se atribuye causa ni se declaran resueltas.
- WPF completa final: `134/134` correctas. El primer intento bajo sandbox no pudo leer el SDK local de Windows;
  la ejecución autorizada fuera del sandbox y su repetición final fueron correctas.
- Build Release completo: correcto, `0` advertencias y `0` errores.
- `dotnet format --verify-no-changes`: correcto.
- `git diff --check`: correcto.

Durante el desarrollo, una primera Core completa detectó dos carreras en pruebas B7A nuevas (auto-invalidación
de publicaciones propias y expectativa no determinista del runner). Se corrigieron los mecanismos y se repitió
la suite completa con el resultado final anterior. Una prueba focal de diagnóstico de hang generó y abortó un
`testhost`; no se inspeccionaron sus artefactos en `TestResults`.

### Investigación acotada de los fallos lifecycle paralelos

La suite usa `[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`; la salida del runner confirmó cuatro
workers. Los fallos observados en las ejecuciones completas fueron:

- `Scenarios42_67_68_69_71_MultiRunDiagnosticsAreSeparatedAndFailureIsIsolated`: tras esperar ambos drains,
  `completed.Lifecycle` fue `Running` en vez de `Stopped`; el stack terminó en
  `CommittedGateProcessingOrchestratorTests.cs:956`.
- `TerminalCoverageLedgerAdmitsExactLimitAndFaultsBeforeNextIdentity`: después de que `WaitForDrainAsync`
  terminara con la excepción esperada, `failed.Lifecycle` fue `FinalReconciling` en vez de `Faulted`; el stack
  terminó en `CommittedGateProcessingOrchestratorTests.cs:90`.

Los archivos B7A modifican Presentation y Visualization.Core; no modifican Processing.Core, los fixtures, el
orquestador, `WaitForDrainAsync`, sus notificaciones ni su cierre. Las nuevas pruebas B7A sí participan en el
paralelismo global y pueden alterar presión y scheduling del proceso, sin compartir owners con esos fixtures.

Reproducción con la misma configuración paralela, sin procesos de test simultáneos:

- worktree B7A: ambos tests juntos pasaron `10/10` series (`20/20` casos);
- worktree detached limpio en `4cb86b6dc1797c7cadc4918c288937a3ce2daec2`: ambos tests juntos pasaron
  `10/10` series (`20/20` casos);
- base: Core completa paralela pasó `3/3` ejecuciones (`1043/1043` cada una);
- B7A: Core completa paralela pasó las dos primeras ejecuciones (`1054/1054` cada una). La tercera no reprodujo
  los fallos lifecycle; falló por un timeout de cleanup en
  `BScanRangeProjectionTests.NewRevisionImmediatelyBeforeCandidateValidationRejectsStaleRaster`, ajeno al diff.
  No se atribuye causa a ese timeout ni se declara resuelto.

Conclusión: no se ha establecido una regresión B7A ni se han reproducido los dos fallos lifecycle de forma
dirigida o comparativa. Tampoco se demuestra con esta muestra que sean incidencias previas deterministas. Se
conserva explícitamente la incertidumbre y su eventual seguimiento pertenece a una investigación separada de
Processing.Core/scheduling de la suite; B7A no modifica esa semántica.

## Riesgos y pendientes

- La transformación real de tamaño/DPI será suministrada por el adaptador B7B; B7A solo acepta y valida el
  contexto neutral.
- La composición productiva debe compartir el artefacto estable con Apply en el Paso 8 sin recompilarlo.
