# Fase 6 B5 — lifecycle headless y budgets deferred

## Objetivo

Cerrar el Paso 5 del núcleo headless mediante límites inspection-scoped independientes, lifecycle material
`Active → Disposing → Disposed` y un cierre ordenado de Apply, epochs live, jobs históricos, observers,
store y autoridad. Presentation, WPF y composición DI general permanecen fuera.

## Resultado

- `GateHeadlessInspectionOptions` reúne, sin sumarlos, los límites de ledgers, bytes estimados, observers,
  candidatas, jobs, workers, buffers históricos y epochs por run. Conserva los defaults previos.
- El store acepta un límite internal de candidatas; autoridad, coordinator, scheduler y live conservan sus
  contadores y rechazos propios. Un rechazo previo a admisión no consume ordinal; los terminales de una
  operación FIFO ya admitida mantienen la reconciliación global existente.
- La autoridad puede cerrar nuevas preparaciones, conservar cada ledger, avanzar `Active → Disposing` y,
  solo tras terminales materiales y `PendingResources.None`, avanzar a `Disposed`. La causa previa permanece
  primaria y Dispose se añade como evidencia append-only.
- `IGateLiveProcessingLifecycle` separa el cierre del owner live de su ingress. Su Dispose aborta reservas de
  epoch, drena workers/callbacks y libera sus pins y leases internas. Las leases externas siguen perteneciendo
  al consumidor y el store espera su liberación en lugar de forzarla.
- `GateHistoricalRunServices.DisposeAsync` comparte una única tarea: cierra admisiones, solicita Stop, drena
  observers, cierra live, entra en lifecycle de ledger, cierra el store mientras aún conserva su ownership,
  verifica el balance estable y cierra la autoridad al final. Los fallos se agregan en orden, preservando el
  primero y continuando cleanup independiente.
- Los diagnósticos de lifecycle declaran explícitamente si son lectura concurrente o balance estable posterior
  al drenaje. La evidencia final se captura antes de que la autoridad libere sus ledgers.
- Se corrigieron terminales históricos sin predecesora o sin materialización para que
  `PreviousViewRetirement` termine en `NotApplicable`, no en `Dormant`.

## Decisiones confirmadas

- No existe presupuesto agregado entre recursos heterogéneos.
- La capacidad de epochs sigue siendo por run y no se transforma en budget inspection-wide.
- `Disposed` no elimina ni reabre `Activated`/`Reclaimed`; exige terminales coherentes y ningún pending owner.
- El store permanece operativo hasta terminar observers; su cierre puede esperar leases externas sin gates de
  Application retenidos.
- Dispose concurrente o repetido observa la misma operación de cierre.

## Archivos modificados

- Application: opciones inspection-scoped, lifecycle/diagnósticos de ledger, orden de cierre de servicios
  históricos, puerto live en sesión y reconciliación de terminales históricos.
- Processing.Core: puerto segregado live y capacidad configurable de candidatas.
- Tests: lifecycle terminal, Dispose repetido, diagnósticos estables, defaults/límites independientes y
  retención por Observer/Lease/Tombstone.

## Pruebas y validaciones

- Focalizadas de ApplicationSession/Apply/lifecycle: 59/59; focalizadas de Apply: 29/29;
  capacidad de candidatas exacta/one-over/recuperación: 1/1.
- Una ejecución de Core completa terminó con un timeout de cleanup en
  `BScanRangeProjectionTests.NewRevisionImmediatelyBeforeCandidateValidationRejectsStaleRaster`, ajena al diff.
  No se atribuye causa ni se declara resuelto.
- La repetición completa posterior de Core pasó 1032/1032.
- WPF completa: 134/134.
- Build Release: correcto, 0 advertencias y 0 errores.
- `dotnet format --verify-no-changes` y `git diff --check`: correctos.

## Riesgos y cuestiones pendientes

- La composición DI pública de estas opciones continúa aplazada al Paso 8; B5 solo aporta el objeto internal y
  los puntos de consumo sin ampliar Contracts públicos.
- Presentation y WPF no forman parte de este bloque.
