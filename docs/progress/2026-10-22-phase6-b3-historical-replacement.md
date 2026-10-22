# Fase 6 — B3 reemplazo histórico headless

## Objetivo y resultado

B3 integra el reprocesado histórico `StoreOnly` con un owner de Application asignado antes de `Admitted` y
conservado hasta la reconciliación terminal. El bloque no dispara trabajo desde `Stop`, no completa el lifecycle
global, no añade budgets globales deferred y no modifica Presentation ni WPF.

## Decisiones confirmadas

- `GateReprocessingProgressSnapshot.StoreOnlyCandidate` es una ampliación aditiva e inmutable. Solo puede estar
  presente en `Completed + StoreOnly`, contiene el descriptor autoritativo completo y admite cobertura seleccionada
  vacía. El scheduler público anterior sigue siendo compatible y puede completar sin receipt; el camino owned de
  B3 lo exige.
- Application depende de los puertos internos segregados `IOwnedGateReprocessingScheduler` e
  `IGateReplacementStore`; no conoce el store concreto. La amistad de ensamblado ya aceptada para Application no
  se amplía.
- La reserva de identidad precede a la admisión. `Bind` transfiere el job sin asignaciones ni callbacks externos
  antes de publicar `Admitted`; materialización, CAS, abandono y reclaim permanecen bajo ownership estable.
- Un segundo intento para la misma inspección/run/plan/revisión retorna `AlreadyRequested` y no crea otro job.
- Run, desired-replacement y CAS se arbitran bajo la misma exclusión del store. El CAS autoritativo evalúa run,
  token, predecesora, candidato, cobertura y capacidad en un único snapshot; autoridad sigue siendo el último
  commit preparado de Apply.
- Tras CAS exitoso nunca se abandona ni reclama la vista activada. El ledger conserva la activación incluso si
  falla trabajo posterior. Si una lease bloquea el retiro de la predecesora, el ledger conserva
  `DeferredByLeases + Observer + Tombstone`: el store sigue siendo el owner físico y el observer conserva su
  responsabilidad terminal mientras observa la finalización del tombstone. El store espera solo las leases que
  bloquean físicamente esa predecesora, completa el reclaim exactamente una vez y solo entonces Application
  retira ambos recursos pendientes; Application no vuelve a competir por el reclaim. Si `Dispose` gana durante
  esa espera, el owner del store espera `CloseAsync`, confirma la liberación física y entrega el mismo terminal
  `Reclaimed` al observer, sin dejar ledger pendiente. Varios observers despertados por el mismo drenaje observan
  un completion idempotente: solo uno muta físicamente y `NotFound` posterior se normaliza a `Reclaimed` dentro
  del ticket lógico que previamente había recibido `Busy`.
- `Disposed` del puerto de reemplazo significa que `InMemoryGateDerivedStore.CloseAsync` ya drenó writers,
  readers y leases y limpió candidatas, vistas, tombstones y claims bajo su gate. Por ello el observer puede
  registrar la transferencia/cleanup como `LogicallyAbandoned -> Reclaimed`, conservar `Disposed` como causa
  secundaria y retirar `Observer`; durante `Disposing`, antes de esa confirmación, el store continúa siendo owner.
- Confirmar cobertura terminal es una mutación versionada incluso con cero trazas. Esto proporciona una revisión
  capturada válida al receipt de cobertura vacía.

## Archivos y validación

Los contratos, scheduler, store, autoridad/ledger, coordinador headless y pruebas deterministas se mantienen en
los proyectos neutrales de Contracts, Processing.Core, Application y Core.Tests. No se añadieron paquetes.

La validación final y el estado Git se registran en el informe de cierre del agente antes del commit; este bloque
permanece sin stage ni commit.

Durante la validación, una ejecución previa de Core terminó con un timeout de cleanup en una prueba B-Scan ajena
al diff. La repetición completa posterior pasó 1022/1022. No se atribuye una causa al timeout ni se considera
resuelto a partir de esta evidencia.

## Riesgos y pendientes

- El disparo automático al sellar/`Stop`, el drenaje integral de lifecycle, los budgets globales deferred y la
  composición productiva pertenecen a bloques posteriores.
- Presentation y WPF permanecen fuera de B3.
