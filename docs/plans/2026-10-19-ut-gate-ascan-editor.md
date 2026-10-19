# Plan ejecutable aceptado — Fase 6: editor y overlay de gates en A-Scan

Fecha: 2026-10-19. Estado: **Aceptado; no implementado**.

Se rige por el
[ADR 0018](../adr/0018-editable-gate-plan-authority.md), ahora **Accepted**, y por los ADR
[0015](../adr/0015-authoritative-inspection-data-store.md) y
[0017](../adr/0017-neutral-ut-gate-processing.md).

## 1. Base comprobada y objetivo

Fases 1–5 están integradas. Existen contratos neutrales, compilación/evaluación de gates, epochs live, store
derivado RAM y reprocesado de runs selladas. Fase 6 añadirá autoridad RAM editable, Preview/Cancel, Apply
linealizable, reemplazo histórico seguro y overlay/editing accesible en A-Scan.

Hechos que condicionan el diseño:

- `ApplicationSession` espera hoy `writer.CommitAsync` y `writer.CommitGapAsync` fuera de su `_gate`; ese
  gate solo gobierna lifecycle y actualización/publicación local posterior y no es frontera RF;
- `CommittedGateProcessingOrchestrator.ActivatePlan` valida y puede asignar mediante `List.Add`; no tiene
  prepare/reserva/rollback ni commit no fallable;
- el store mantiene una sola `ActiveView`;
- `StoreOnly` no activa por sí mismo, y `IGateReprocessingJob.Completion` necesita un owner externo estable.

No se implementan en este plan Undo, persistencia durable/outbox, C-Scan, formatos externos, PDF, movimiento,
hardware nuevo ni 3D. No se añaden paquetes sin aprobación.

## 2. Arquitectura objetivo

### 2.1 Autoridad, draft y resultados

Application poseerá una autoridad RAM inspection-scoped para committed, compilado, revisión y ledger.
Presentation poseerá solo el draft y snapshots inmutables. Preview/Cancel no tendrán efectos productivos; Apply
usará revisión esperada y rechazará stale sin merge. `Accepted` significará committed+epoch confirmados, no
histórico completo ni vista reemplazada.

### 2.2 Coordinador serial y frontera RF

Se introducirá un coordinador serial asíncrono común poseído por Application; el nombre C# no se fija en este
plan. Por él pasarán commits RF, commits gap, Apply/CommitGatePlanEpoch y cambios de run que invaliden candidatas.
Será compatible con `await`, sin `Monitor` a través de awaits, FIFO o explícitamente libre de starvation, sin
callbacks/observers/UI reentrante bajo exclusión. Cancelación antes de entrar no muta; tras el punto de no
retorno se completa reconciliación aunque el waiter se cancele. Dispose espera operaciones aceptadas.

Commit RF/gap: entrar; validar lifecycle/generación; esperar writer; registrar revisión confirmada; construir
publicación; salir; notificar. Apply: preparar trabajo independiente; entrar; revalidar; observar la última RF
confirmada; calcular N+1; preparar epoch/ledger/store/token; cruzar punto de no retorno; confirmar operaciones ya
preparadas; salir; publicar.

Carrera obligatoria: si writer confirmó N pero la sesión aún no actualizó/notificó, Apply espera el coordinador,
observa N y usa N+1. La notificación de N conserva el plan anterior. RF confirmada, estado local notificado y
callbacks observables son estados distintos.

### 2.3 Preparación y orden de gates

El nuevo puerto `PrepareEpoch` devolverá una reserva opaca. Toda validación, asignación y capacidad exacta se
resuelve en Prepare. Commit consume una vez, no asigna/revalida y es contractualmente no fallable; Abort libera
una vez. Default, doble Commit, Commit tras Abort, doble Abort, cancelación y Dispose quedan definidos. El worker
no ve reservas. La reserva pinnea la run contra drain, retirada y Dispose hasta Commit/Abort; el cierre deja
cero pins. Primero se caracterizará `ActivatePlan` actual.

Epoch, store y transición de run comparten resultados deterministas:
`InvalidReservation`, `Committed`, `AlreadyCommitted`, `Aborted`, `AlreadyAborted` y
`OwnerOrGenerationMismatch`. Commit tras Abort devuelve `AlreadyAborted`; Abort tras Commit devuelve
`AlreadyCommitted`. La clasificación aplica reserva default, owner/generación, estado y operación, en ese orden,
para no inferir estado mediante credenciales ajenas. No lanzan por control ordinario. Commit no asigna, espera, hace I/O, callbacks ni
revalidación externa. Dispose aborta/reconcilia toda reserva Prepared.

La preparación agregada del store incluye capacidad de ledger, slot/token, committed, geometría, predecesora,
inspection, run, plan, revisión y generación, pero conserva owners: Application posee ledger/committed/geometría
neutral, live el epoch/pin y store token/predecesora/candidata. Cada owner prepara su capacidad; el Commit
coordinado consume una vez sin fallo/callback y Abort hace rollback exacto. Cancelación, Stop y Dispose
reconcilian. Bajo store se observa `ActiveView`, se captura predecesora y se instala token con toda la identidad.
A activada antes de B hace que B capture VA; token B primero invalida el CAS A.

La capacidad live configurada es finita; actualmente el valor por defecto es ocho epochs/run, no una garantía
pública. Sin slot, Prepare devuelve `CapacityExceeded(resource: LiveEpochs)` antes de revisión/token/committed,
geometría, ledger o Accepted. Abort libera un slot provisional y permite reintento si la revisión sigue vigente;
un epoch confirmado no libera slot durante esa run y `LiveEpochLimitReached` no es reintentable en ella.

| Orden | Gate/camino | Restricción |
| --- | --- | --- |
| 1 | coordinador/autoridad Application | Primera exclusión para RF, Apply, sellado y run switch |
| 2 | reserva preparada del orquestador live | Solo coordinador → live; nunca live → coordinador |
| 3 | reserva preparada de store/token/ledger | Solo coordinador → live → store; nunca store → coordinador |
| 3, cambio de run | reserva de transición → store | Commit comparte con CAS token, ownership, ActiveView, visibilidad y generación |
| Independiente | CAS/abandono bajo gate store común | No adquiere live ni coordinador |
| Posterior | callbacks/observers | Sin gates |

`ApplicationSession._gate` no se mantiene durante awaits del writer. Si se captura lifecycle/generación bajo él,
se libera antes de entrar al coordinador y se revalida sin introducir ciclos; preferiblemente el mínimo estado
de admisión reside en el coordinador.

### 2.4 Histórico, Completion y runs

Fase 6 usa `StoreOnly` y un descriptor candidato inmutable/sin leases. Cada job admitido obtiene inmediatamente
un observer terminal Application no cancelable por comando/UI/waiter. Consume Completion una vez, reconcilia
ledger, intenta CAS si sigue deseado o abandona/reclama. Dispose espera esos observers; el scheduler puede
retirar el job sin perder cleanup.

Antes de admitir se prepara un slot no fallable para ledger/observer. La transferencia del handle al owner es
reconciliable: después de `Admitted` el registro no asigna ni falla; de lo contrario no se admite.

Como solo existe una `ActiveView`, abrir/cambiar run productiva invalida bajo el coordinador toda capacidad de
activar candidatas anteriores. Pueden terminar StoreOnly para cleanup/diagnóstico, pero quedan
`Superseded/NotActivatable`. Nunca reemplazan la vista global de la nueva run. ActiveView por run se aplaza.
La vista anterior puede quedar físicamente como predecesora, pero se publica stale/no-visible por `RunId`; el
primer Apply nuevo la captura al instalar token y solo la retira después de activar su reemplazo.

La transición de run se prepara sin publicar y se confirma bajo el mismo gate store de CAS: invalida tokens y
candidatas, impide CAS y marca la vista saliente Stale/NotVisible antes de publicar la nueva run. Si CAS gana
primero, la transición observa y oculta esa vista; si la transición gana, CAS devuelve `RunSuperseded`. Leases
pueden cerrar sin reclamación inmediata y callbacks tardíos no restauran token ni visibilidad.

### 2.5 Ownership de candidatas

| Estado | Owner |
| --- | --- |
| Reserved | store/scheduler; no activable |
| Materializing | scheduler/store; commits/cobertura |
| Available | físico store; decisión observer Application |
| Activating | store bajo CAS; abandono excluido |
| Activated | store; no reclamable como candidata |
| Abandoning | store; CAS excluido |
| LogicallyAbandoned | terminal Application; store/tombstone asume reclaim diferido si Busy |
| Reclaimed | terminal físico, liberación única |

El lifecycle global se modela por separado como `Active → Disposing → Disposed`. Al entrar en `Disposing` no se
admiten operaciones nuevas, pero el ownership material se conserva mientras terminan o se cancelan trabajos y se
drenan observer, leases y tombstones. `Disposed` exige cero recursos pendientes y ningún trabajo material activo;
no reabre ni reescribe terminales: `Activated` y `Reclaimed` permanecen visibles, y `LogicallyAbandoned` debe
alcanzar reclaim físico antes del cierre global. La causa de Dispose es tipada y append-only.

CAS y abandono comparten exclusión. Abandono primero impide activar; activación primero impide reclamar; nunca se
reclama una vista activa. `CandidateRetirement=LogicallyAbandoned` confirma abandono lógico: si reclaim fue Busy,
Application no reintenta y el tombstone termina la liberación. La predecesora se retira una sola vez después de
activar el reemplazo.

CAS compara conjuntamente descriptores completos de predecesora/candidata, InspectionId, RunId, GatePlanId,
GatePlanRevision, token/generación deseada y generación vigente de run; exige ownership `Available`, candidata
no abandonada/reclamada/dispuesta, predecesora activa y capacidad vigente. Devuelve `Activated`,
`TokenSuperseded`, `RunSuperseded`, `PredecessorMismatch`, `CandidateMismatch`,
`CandidateNotAvailable`, `AlreadyActivated`, `CandidateAbandoned` o `Disposed`. `AlreadyActivated` es éxito
idempotente de la misma candidata ya activa y no repite activación ni retirada; rechazar no altera `ActiveView`.
La clasificación es única y ordena Dispose, run, token/generación deseada, replay idempotente de la misma
candidata ya activa, predecesora, candidata, ownership, capacidad y éxito. El replay se adelanta porque la
predecesora anterior ya no es `ActiveView`; no existen factories de rechazo alternativas.

### 2.6 Ledger

La tabla normativa completa de estados, owners, supersesión y terminales está en ADR 0018. Distingue Dormant
latente de NotApplicable terminal y separa Commit, LiveEpoch, HistoricalAdmission, HistoricalExecution,
CandidateCoverage, Activation, CandidateRetirement y PreviousViewRetirement. Configura límites de revisiones
terminales y bytes por inspección; reserva antes de revisión/token y devuelve CapacityExceeded sin publicación.
Solo evicta terminales elegibles no referenciados por observer, job, CAS, candidata/tombstone, lease, lifecycle
o diagnóstico pendiente.

Apply solo se admite en Running con admisión abierta, capacidad, frontera posterior al target y plan no
duplicado. Los estados de cierre devuelven un rechazo neutral antes del commit. El incremento caracteriza qué
estados del enum son realmente observables. Si Stop gana, Apply no acepta; si Apply ya cruzó el punto de no
retorno, Stop drena su epoch. Dispose solapado conserva Completion/descriptor/cleanup aunque terminalice trabajo
pendiente como cancelado o faulted.

### 2.7 WPF

Cada `BeginCapture` crea un nonce monotónico único. La generación incluye ese nonce, `RunId`,
`TraceIdentity`, `GatePlanRevision`, `GateId`, handle,
identidad/revisión de anchor y versión de transformación. Cambiar cualquiera de ellos, pan, zoom, resize, DPI o
perder captura/foco invalida generación, cancela captura, descarta eventos tardíos, restaura el último Preview
estable compatible, no hace Apply y publica un snapshot coherente. Nunca mezcla versiones de coordenadas.

Tiempo/umbral se derivan del eje original y snapshots neutrales, no de puntos reducidos. Los handles son
focalizables y exponen Automation, teclado y estado observable.

El control recibe una única DependencyProperty con un snapshot inmutable que reúne identidad completa,
GateId/handle, anchors, límites/umbral, geometría, selección/foco/Automation, unidades/rangos,
viewport/transformación/DPI/versionado, nonce y estado Preview/Committed/Conflict/Stale. Render, hit-test,
edición, foco, selección y Automation consumen esa misma instancia; se prohíben bindings de revisiones
distintas. Las salidas son intenciones neutrales con la generación observada.

## 3. Secuencia vertical ejecutable

### Paso 0 — decisión

Completado documentalmente: ADR 0018 auditado y aceptado. No hay implementación productiva.

### Paso 1 — contratos y caracterización

Caracterizar commits RF/gap, `ActivatePlan`, `StoreOnly` y lifecycle actuales. Definir contratos del
coordinador, reservas preparadas, descriptor candidato, token/CAS y ownership. Sin ruta Apply productiva.

### Paso 2 — autoridad, draft y Preview

Implementar autoridad RAM y Presentation con Preview/Cancel. Sin Apply productivo ni efectos.

### Paso 3 — frontera RF y epoch preparado

Implementar coordinador serial común; migrar commits RF/gap; añadir reservas de epoch y ledger/token/store;
añadir transición preparada de run bajo store y completar CommitGatePlanEpoch. Habilitar el caso de uso Apply
headless únicamente al terminar todo el paso; su
composición productiva/UI permanece cerrada hasta el Paso 8, y cada paso posterior conserva resultados tipados
sin fingir efectos todavía no instalados.

### Paso 4 — histórico y reemplazo

Añadir StoreOnly+descriptor, observer terminal Application, CAS, abandono/reclamación, supersesión y cambio de
run. Terminar sin candidatas huérfanas.

### Paso 5 — lifecycle completo

Completar A/B/C deferred, Stop de run, Dispose global, saturación y budgets independientes.

### Paso 6 — anclas y geometría neutral

Copiar anclas exactas sin filtrar leases y proyectar geometría desde el eje original.

### Paso 7 — overlay/edición WPF y accesibilidad

Implementar snapshot visual, render, hit-test, captura versionada, teclado y Automation.

### Paso 8 — composición y aceptación

Componer DI/lifetimes y ejecutar aceptación integral, arquitectura, format y build/test Release.

Cada paso debe compilar y pasar sus pruebas sin introducir dos autoridades; Apply no queda parcialmente
productivo entre pasos.

## 4. Matriz obligatoria de pruebas

- Frontera RF: RF confirmada y actualización local bloqueada; gap equivalente; ambos órdenes Apply/commit; sin
  callbacks bajo coordinador.
- Epoch preparado: prepare fallido, exact-limit/one-over, Commit, Abort, doble Commit, Commit tras Abort, doble
  Abort, cancelación/Dispose con reserva viva, pin contra drain/retirada y Commit sin asignaciones/fallos.
- Predecesora/token: CAS A antes de B; token B antes de CAS A; B recaptura VA tras A; A falla tras token B.
- Completion: waiter cancelado, observer continúa, Completed+StoreOnly consumido, candidato reclamado y Dispose
  espera; fault/cancel entre admisión y registro nunca deja un job sin owner.
- Ownership: CAS/abandono en ambos órdenes, Busy, tombstone completa, activa nunca reclamada y retiro previo una
  vez.
- Cambio de run/CAS: CAS empieza y gana antes del commit de cambio; la transición marca Stale/NotVisible antes
  de publicar; transición gana antes del CAS; CAS tardío devuelve RunSuperseded; callbacks tardíos no restauran
  token ni visibilidad.
- Run: nueva run con VA activa, VA stale/no-visible, finalización tardía anterior, primer CAS/retiro y cleanup.
- WPF: cambio de traza, revisión, GateId/handle, anchor, pan/zoom/resize/DPI, pérdida de captura/foco y eventos
  tardíos ignorados; dos capturas con tupla idéntica se distinguen por nonce.
- Lifecycle: Stop de run admite job y retorna; Dispose empieza inmediatamente; Stop histórico cancela o deja
  terminar; observer consume Completion; descriptor se activa o reclama según run; store no se dispone antes.
- Apply/lifecycle: Apply antes de Stop; Stop gana antes del punto de no retorno; Apply cruza antes de Stop; Apply
  contra Dispose; ninguna ruta publica Accepted parcial.
- Ledger: estado inicial y transiciones de cada subestado; transición ilegal; terminal no reabierto; Dormant
  frente a NotApplicable; exact-limit/one-over por revisiones y bytes; retención por observer, CAS, tombstone,
  lease y lifecycle; evicción determinista; CapacityExceeded no consume revisión/token.
- Reservas: default, doble Commit, Commit tras Abort, doble Abort, Abort tras Commit, owner/generación
  incorrectos y Dispose con reserva Prepared.
- Epochs configurados N: epoch inicial + N−1 Apply aceptados; siguiente Apply rechazado sin revisión/token/
  ledger; Abort devuelve slot provisional; epoch confirmado no devuelve slot hasta retirar la run.
- Vertical: antes de Paso 8 la composición/UI rechaza Apply sin committed, epoch, ledger ni efectos.
- Integración: Apply→epoch→StoreOnly al sellar→Completion→CAS/abandono→retiro; balances finales cero.

Las pruebas observan estado autoritativo, no solo contadores, y coordinan carreras con barreras/TCS, sin sleeps,
polling, hooks productivos ni reflexión sobre miembros privados.

## 5. Criterios de entrada y salida

Entrada: ADR 0018 aceptado; Fases 1–5 verdes; árbol limpio; ownership por archivo; sin paquetes nuevos.

Salida: autoridad única; RF/gap/Apply/run switch serializados; reservas exactas reconciliadas; Preview/Cancel sin
efectos; Apply committed+epoch atómico; histórico StoreOnly con owner terminal; CAS/abandono seguro; cambio de run
invalida candidatas; interacción WPF versionada y accesible; Stop/Dispose sin huérfanos. Build Release, Core/WPF,
arquitectura, format, diff, UTF-8, enlaces y trazabilidad verdes.

Riesgos P1: frontera RF falsa, epoch visible a medias, activación de run anterior, pérdida de ownership de
Completion, deadlock. P2: reserva no exacta, reclaim doble/huérfano, mezcla de transformaciones, WPF como
autoridad. P3: nomenclatura C# definitiva y valores finos de interacción, deliberadamente aplazados.

Rama futura recomendada tras aceptar el ADR: `feature/ut-gate-ascan-editor`.
