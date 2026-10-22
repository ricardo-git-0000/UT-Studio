# Fase 6 B4 — handoff histórico de run sellada

## Objetivo

Conectar el recorrido headless interno `Apply → sellado → StoreOnly → Completion → CAS/abandono` y cerrar
de forma ordenada los jobs, observers y recursos físicos que introduce, sin incorporar Presentation, WPF,
composición general ni budgets globales deferred.

## Resultado

- `ApplicationSession.StopAsync` cierra Apply y el FIFO, drena RF/Gap/Apply, cierra el ingress, sella el writer,
  publica y drena la reconciliación live y, solo entonces, realiza el handoff histórico directamente. El FIFO
  ya cerrado no se reabre.
- `StopAsync` espera únicamente la admisión o su rechazo tipado; el observer conserva el ownership hasta el
  terminal histórico. Cancelar un waiter de Stop no cancela cleanup ni el job admitido.
- El estado internal `FinalReconciling` cierra Apply/desired-replacement y autoriza solo el candidato ya
  seleccionado. Una nueva transición de run puede sustituirlo; el terminal antiguo solo completa `Stopped`
  si coinciden identidad, run y generación.
- El sello aporta `FinalRevision` y los límites inclusivos autoritativos. Cobertura vacía se terminaliza como
  `NotApplicable`; no se representa artificialmente mediante `[0,0]`.
- En A→B→C solo C pasa de `Deferred` a admisión. A y B quedan `Superseded/NotApplicable`, y todos los epochs
  live de la run quedan `Drained` bajo la exclusión de autoridad.
- El registry de observers pertenece al scope de inspección, se comparte entre runs, reserva antes de llamar al
  scheduler, tiene capacidad finita y elimina cada entrada al terminar el observer. `CloseAdmission` impide
  nuevas reservas y `DrainAsync` espera las ya admitidas.
- La estimación de bytes del ledger reserva desde Prepare espacio estable para las ocho causas canónicas; la
  reconciliación terminal no puede sobrepasar a posteriori el budget que autorizó el commit.

## Ownership y cierre

`GateHistoricalRunServices`, creado por la composición interna de la inspección y entregado a
`ApplicationSession`, es el owner del coordinator, lifecycle del scheduler, puerto segregado del store y
authority owner. Su cierre idempotente ejecuta:

1. cerrar admisión del registry y scheduler;
2. solicitar Stop al scheduler y esperar sus jobs;
3. drenar observers Application, incluidos cleanup y tombstones dependientes de leases;
4. disponer scheduler;
5. disponer store;
6. cerrar y esperar authority.

No se espera bajo gates. El store permanece vivo mientras un observer puede activar, abandonar, reclamar o
esperar la liberación de una lease. Una activación confirmada no entra en el camino de abandono. Si falla más de
una etapa de cierre, se conserva el primer error y los restantes se adjuntan en un `AggregateException`.

## Contratos y encapsulación

- Los puertos `IOwnedGateReprocessingLifecycle` e `IGateReplacementStoreLifecycle` son internal y segregados.
- Application depende de esos puertos y no de `InMemoryGateDerivedStore`.
- No cambia ninguna API pública ni se añade `InternalsVisibleTo`.
- El resultado `ObserverCapacityExceeded` es internal y no se confunde con capacidad semántica del derived store.

## Archivos modificados

- Application: sesión, servicios del handoff, coordinator histórico, apply services y autoridad/ledger.
- Processing.Core: lifecycle del scheduler, lifecycle segregado del replacement store y transición
  `FinalReconciling`.
- Pruebas Core: autoridad/bytes, transición de run, registry, cobertura vacía y recorrido productivo de sesión.

## Pruebas y validaciones

Las pruebas son deterministas y usan `TaskCompletionSource`/barreras, sin sleeps. Cubren selección A→B→C,
capacidad del registry, Completion inmediata, waiter cancelado, Faulted/Cancelled, sellado y límites, cobertura
vacía, retorno temprano de Stop, CAS/abandono existentes de B3, transición de run en ambos órdenes, tombstones,
leases y reclaim único.

- focalizadas B4/B3/contratos: 105/105, 45/45 y 3/3 en las selecciones ejecutadas;
- Core completa: 1029/1029;
- WPF completa: 134/134;
- solución Release: correcta, 0 warnings y 0 errores;
- `dotnet format --verify-no-changes`: correcto;
- `git diff --check`: correcto.

## Riesgos y pendientes

- La composición DI general y la configuración externa de capacidades pertenecen al Paso 8.
- Los budgets globales deferred permanecen fuera de B4.
- Presentation y WPF permanecen sin cambios.
