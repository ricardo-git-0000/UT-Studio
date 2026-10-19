# ADR 0018 — Autoridad y publicación del plan de gates editable

Fecha: 2026-10-19. Estado: **Accepted**.

Complementa, sin modificar retrospectivamente, los ADR
[0015](0015-authoritative-inspection-data-store.md) y
[0017](0017-neutral-ut-gate-processing.md). Plan ejecutable:
[Fase 6 — editor y overlay A-Scan](../plans/2026-10-19-ut-gate-ascan-editor.md).

## Contexto

Las Fases 1–5 ya proporcionan modelo y revisiones de gates, compilación/evaluación neutral, epochs live, store
derivado RAM y reprocesado de runs selladas. Fase 6 continúa **no implementada**.

El estado real impide usar el `_gate` de `ApplicationSession` como frontera RF: `writer.CommitAsync` y
`writer.CommitGapAsync` se esperan fuera de ese gate. `_gate` gobierna lifecycle y la actualización/publicación
local posterior; no serializa la confirmación autoritativa en el store con Apply. Asimismo,
`CommittedGateProcessingOrchestrator.ActivatePlan` valida y puede asignar mediante `List.Add`; hoy no ofrece
prepare, reserva, rollback ni un commit contractualmente no fallable.

El scheduler histórico ya admite `StoreOnly`, pero el store tiene una única `ActiveView`. No existe una
transacción distribuida entre sesión, procesamiento live, scheduler y store. La decisión siguiente define los
contratos que Fase 6 deberá introducir; no afirma que existan actualmente.

## Decisión propuesta

### Autoridad, draft y alcance

Un servicio neutral poseído por Application será la única autoridad RAM de `CommittedPlan`, compilado, revisión
y ledger de Apply, con clave `(InspectionId, GatePlanId)`. Sobrevive a runs y vistas dentro del proceso, no a un
reinicio. Presentation conserva solamente draft y snapshots inmutables; WPF, visualización y schedulers no
asignan revisiones ni poseen el plan comprometido.

Preview modifica solo inicio, fin/duración y umbral de gates existentes. Cancel restaura el committed vigente.
Ninguno compila autoritativamente, crea epochs, invalida vistas ni admite reprocesado. Apply usa revisión
esperada, rechaza stale sin merge y asigna revisión solo al aceptarse.

`Accepted` significa que committed y epoch live quedaron confirmados; no promete finalización histórica ni una
vista nueva activa. El punto de no retorno se cruza únicamente después de preparar todos sus recursos.

### Coordinador serial y frontera RF

Application poseerá un nuevo coordinador serial asíncrono común —el nombre C# queda abierto—. Pasarán por él,
obligatoriamente, commits RF, commits de gap, `Apply/CommitGatePlanEpoch` y transiciones de run que invaliden la
capacidad de activar candidatas. No se reutiliza ni se atribuye esta función al `_gate` actual de sesión.

El coordinador proporciona exclusión compatible con `await` (nunca `Monitor` a través de `await`), FIFO o una
política explícita libre de starvation, y no ejecuta callbacks, observers ni llamadas reentrantes a UI mientras
posee la exclusión. Cancelar antes de entrar no altera estado. Una operación admitida que cruza su punto de no
retorno completa su reconciliación aunque se cancele el waiter externo. Dispose cierra admisión y espera todas
las operaciones aceptadas. Preferentemente posee el mínimo estado de lifecycle/generación necesario para
admitir y revalidar, evitando tomar `_gate` de sesión desde dentro.

Un commit RF o gap:

1. entra en el coordinador;
2. comprueba lifecycle y generación de sesión/run;
3. espera `writer.CommitAsync` o `writer.CommitGapAsync`;
4. registra en el coordinador la revisión RF confirmada autoritativa;
5. construye la publicación local posterior;
6. sale del coordinador;
7. publica callbacks y observers.

Apply prepara fuera del coordinador cuanto no dependa de la frontera. Después:

1. entra en el coordinador;
2. revalida inspección, run, revisión esperada y lifecycle;
3. observa la última revisión RF confirmada por cualquier commit anterior;
4. calcula `effectiveFromInclusive`;
5. prepara/reserva epoch, ledger y token/store con rollback exacto;
6. cruza el punto de no retorno;
7. confirma con operaciones ya preparadas epoch, token, committed, geometría y ledger inicial;
8. sale del coordinador;
9. publica `Accepted` y observers.

Todo commit RF/gap posterior requiere el mismo coordinador, por lo que no puede confirmarse entre la lectura de
la frontera y el registro del epoch. Si el writer confirma N antes de actualizar el estado local, Apply espera,
observa N y fija `effectiveFromInclusive=N+1`; la notificación posterior de N conserva el plan anterior. La
frontera depende de RF confirmada en store, no del estado local notificado ni de cuándo la UI recibe callbacks.

### Epoch y publicación preparados

Se introducirá un puerto conceptual de epoch preparado. `PrepareEpoch` devuelve una reserva opaca y realiza toda
validación, asignación potencial y reserva de capacidad exacta. `Commit` consume una reserva una sola vez, no
asigna, no revalida y es contractualmente no fallable; `Abort` la libera una sola vez. Se definen default,
doble Commit, Commit tras Abort, doble Abort, cancelación y Dispose. Toda reserva viva se reconcilia y el worker
live no observa epochs sin confirmar. La reserva pinnea la run contra drain, retirada y Dispose hasta Commit o
Abort; Stop/Dispose cierran nuevas reservas y esperan o abortan contractualmente las vivas, dejando cero pins.

Epoch, store y transición de run usan la misma máquina tipada de reserva:

| Operación/estado | Resultado |
| --- | --- |
| reserva default/no inicializada | `InvalidReservation` |
| primer Commit sobre `Prepared` válido | `Committed` |
| segundo Commit | `AlreadyCommitted` |
| primer Abort antes de Commit | `Aborted` |
| segundo Abort | `AlreadyAborted` |
| Commit después de Abort | `AlreadyAborted` |
| Abort después de Commit | `AlreadyCommitted` |
| owner o generación distintos de los reservados | `OwnerOrGenerationMismatch` |
| Dispose con reserva `Prepared` | Abort/reconciliación obligatoria |

Estos resultados no lanzan por flujo de control ordinario. Commit no asigna memoria, no hace I/O, no espera,
no invoca callbacks ni revalida condiciones externas: consume una reserva ya validada y devuelve únicamente el
resultado determinista de su estado. Cancelación antes de Prepare no muta; después de preparar obliga a Commit o
Abort incluso si se cancela el waiter.

Antes de cambiar `ActivatePlan`, el incremento caracterizará su validación, asignación y conducta vigente. El
nuevo contrato no se presenta como capacidad existente.

La preparación agregada del store incluye, como payload coordinado, capacidad preasignada de ledger, slot/token
deseado, snapshot committed, geometría, descriptor de predecesora, inspección, run, plan, revisión y generación,
sin transferir ownership entre capas: Application posee ledger/committed/geometría neutral; live posee epoch/pin;
el store posee token, predecesora y capacidad de activación/candidata. Prepare realiza toda validación y
asignación fallable en el owner correspondiente y pinnea owner/generación. El Commit coordinado consume una vez
las reservas bajo el orden común, sin asignación, espera, I/O, revalidación externa ni callback. Abort revierte
exactamente capacidad y pins en cada owner. Cancelación, Stop y Dispose reconcilian toda reserva preparada;
doble consumo devuelve los resultados tipados anteriores.

La predecesora no se captura anticipadamente: durante Prepare se reserva capacidad, pero bajo el gate del store,
al instalar el token, se observa la `ActiveView`, se captura como predecesora y se asocian inspección, run, plan,
revisión y generación. Si A activa antes de aceptar B, B captura VA; si B instala primero su token, el CAS
posterior de A falla por token obsoleto.

El orquestador actual tiene capacidad finita y hoy configura por defecto ocho epochs por run; ocho no es una
garantía pública. `PrepareEpoch` usa la capacidad configurada. Sin slot devuelve
`CapacityExceeded(resource: LiveEpochs)` antes del punto de no retorno: no consume `GatePlanRevision`, no
instala token y no publica committed, geometría, ledger parcial ni `Accepted`. Presentation muestra un rechazo
de capacidad explícito. Un Abort de reserva provisional libera su slot y permite reintentar con la misma
revisión esperada si continúa vigente. Un epoch confirmado no libera capacidad antes de retirar la run:
`LiveEpochLimitReached` es estructural y no reintentable en esa run; requiere otra run o configuración futura.

### Transición preparada de run frente a CAS

Application coordina una reserva preparada de transición, pero su Commit se confirma bajo el mismo gate del
store que CAS. Prepare valida run saliente/entrante, reserva capacidad fallable e identifica generación esperada,
tokens, candidatas y `ActiveView` afectados sin alterar estado observable. Bajo el gate del store, Commit:

1. revalida la generación esperada;
2. invalida tokens deseados de la run saliente;
3. marca no activables sus candidatas pendientes e impide sus nuevos CAS;
4. marca `Stale/NotVisible` cualquier vista saliente ya activada;
5. registra la selección interna de la nueva run;
6. consume la reserva una vez.

Después libera store y solo entonces publica la nueva run y callbacks. Si CAS gana primero, activa mientras la
run anterior sigue vigente; el Commit posterior observa la activación y la vuelve `Stale/NotVisible` antes de
publicar la nueva run. Si la transición gana, invalida primero token, ownership y capacidad; el CAS tardío
devuelve `RunSuperseded` y no cambia `ActiveView`. Token, ownership, ActiveView, visibilidad y generación de
run quedan ordenados por ese gate común; el coordinador ordena la transición respecto de RF y Apply.

Nunca se publica una run con la vista anterior visible ni se activa una candidata de una run no vigente.
`Stale/NotVisible` no reclama físicamente: leases existentes cierran según ownership. Callbacks tardíos no
restauran visibilidad, token ni capacidad de activación.

### Orden de exclusiones

| Orden / camino | Exclusiones permitidas | Regla |
| --- | --- | --- |
| Autoridad/commit RF/Apply/run | coordinador Application | Primera exclusión; puede esperar writer y orquestar reservas |
| Epoch live preparado | coordinador → gate live | Nunca live → coordinador; reserva invisible hasta Commit |
| Store/token/ledger preparado | coordinador → gate live → gate store | Nunca store → live/coordinador; predecesora se captura al instalar token |
| Sellado | captura generación bajo `_gate`, lo libera y usa coordinador | Revalida sin crear sesión → coordinador mientras exista el camino inverso |
| Cambio de run | coordinador → reserva de transición → gate store | Commit de token/ownership/ActiveView/visibilidad/generación comparte gate con CAS |
| CAS o abandono | gate store | Comparten exclusión; no llaman al coordinador ni al live |
| Callbacks/publicación | ninguno | Siempre después de liberar exclusiones |
| Dispose | cierra y drena coordinador; luego recursos internos | Espera operaciones y observers terminales admitidos |

El `_gate` de lifecycle no se conserva durante awaits del writer ni es autoridad RF. No hay caminos
store→coordinador, live→coordinador, callbacks bajo exclusión ni espera circular.

### Reprocesado, token, run y Completion

Todo reemplazo iniciado por Fase 6 usa `StoreOnly`. Al completar con cobertura terminal produce un descriptor
inmutable y sin leases con manifest, `CapturedStoreRevision`, scope, candidata, inspection/run,
plan/revisión/firma y `ReplacementToken`. Cancelled, Faulted y `ActivateOnComplete` no lo producen.

El store mantiene una única `ActiveView`; durante Fase 6 está ligada, igual que el token deseado, a la run
productiva activa. Abrir o cambiar la run productiva pasa por el coordinador e invalida la capacidad de activar
candidatas pendientes de la anterior. Esos jobs pueden terminar `StoreOnly` para diagnóstico y cleanup, pero
quedan `Superseded/NotActivatable` y nunca sustituyen la vista global. Una `ActiveView` por run queda fuera de
Fase 6. La vista activa de la run anterior puede conservarse físicamente como predecesora, pero se publica
`Stale/NotVisible` por mismatch de `RunId` y nunca como datos de la nueva run. El primer Apply de la nueva run
la captura como predecesora física al instalar su token y la retira una sola vez solo tras activar el reemplazo.

Antes de admitir, Application prepara capacidad no fallable para ledger y registro del observador terminal. La
admisión transfiere el handle a ese owner como una única operación reconciliable: tras `Admitted`, instalar el
observer no asigna ni falla; si no puede garantizarse, no se admite. Por cada `IGateReprocessingJob` admitido,
Application crea inmediatamente un observador terminal propio. No usa
el token del comando, UI o waiter, consume `Completion` exactamente una vez, reconcilia ledger y hace CAS si el
descriptor sigue deseado o abandono/reclamación en otro caso. Registra fallos secundarios sin ocultar el
principal. Cancelar al caller no cancela ownership ni cleanup; Dispose espera todos los observers admitidos. La
retirada interna del job por el scheduler no transfiere esta obligación.

El CAS verifica conjuntamente bajo el gate del store: descriptor completo de predecesora, descriptor completo
de candidata, `InspectionId`, `RunId`, `GatePlanId`, `GatePlanRevision`, token/generación deseada,
generación vigente de run, ownership exactamente `Available`, candidata no abandonada/reclamada/dispuesta,
predecesora todavía activa y capacidad de activación vigente. Sus resultados mínimos son `Activated`,
`TokenSuperseded`, `RunSuperseded`, `PredecessorMismatch`, `CandidateMismatch`,
`CandidateNotAvailable`, `AlreadyActivated`, `CandidateAbandoned` y `Disposed`. Ningún rechazo modifica
`ActiveView`.

### Máquina de ownership de candidatas

| Estado | Propietario y semántica |
| --- | --- |
| `Reserved` | store/scheduler; capacidad reservada, aún no activable |
| `Materializing` | scheduler/store; commits y cobertura en progreso |
| `Available` | owner físico store; observer Application debe decidir; descriptor inmutable sin leases |
| `Activating` | store bajo la exclusión CAS; abandono excluido |
| `Activated` | store; nunca reclamable como candidata no activa |
| `Abandoning` | store; CAS excluido |
| `LogicallyAbandoned` | terminal para Application; si reclaim da `Busy`, store/tombstone posee la reclamación física |
| `Reclaimed` | terminal físico; recursos liberados exactamente una vez |
| `Disposed` | terminal global durante Dispose |

CAS y abandono/reclaim usan la misma exclusión. Abandonar primero impide activar; activar primero impide
reclamar; una vista activa nunca se reclama; no hay doble liberación ni candidata perdida.
`CandidateRetirement=Applied` significa que se aceptó el abandono lógico. Ante `Busy`, Application no vuelve a
competir: el tombstone del store completa después la reclamación. `PreviousViewRetirement` ocurre exactamente
una vez y solo después de activar el reemplazo.

### Ledger, sellado y Dispose

El ledger RAM es monotónico, acotado y no durable. `Dormant` es latente y no terminal: espera una decisión
anterior todavía no tomada. `NotApplicable` es terminal: esa etapa no corresponde a la revisión y nunca se
reabre. Cada efecto tiene owner y clave idempotente.

| Subestado | Inicial | Transiciones permitidas | Terminales | Owner / supersesión / fallo posterior |
| --- | --- | --- | --- | --- |
| Commit | `Preparing` | `Preparing → Accepted` o rechazo | `Accepted`, `ValidationFailed`, `Conflict`, `CapacityExceeded`, `CancelledBeforeCommit`, `FaultedBeforeCommit` | autoridad Application; Accepted nunca revierte |
| LiveEpoch | `Dormant` antes de Accepted | `Dormant → Registered → Active → Drained` | `Drained`, `NotApplicable`, `Superseded`, `Cancelled`, `Faulted` | coordinador/live; fallo posterior no revierte committed |
| HistoricalAdmission | `Dormant` | `Dormant → Deferred → Admitting → Admitted` | `Admitted`, `NotApplicable`, `Superseded`, `Cancelled`, `Rejected`, `Faulted` | Application; token/run no vigente supersede |
| HistoricalExecution | `Dormant` | `Dormant → Queued → Running → Completed` | `Completed`, `NotApplicable`, `Superseded`, `Cancelled`, `Faulted` | scheduler/observer; resultado tardío se reconcilia |
| CandidateCoverage | `Dormant` | `Dormant → Building → Complete` | `Complete`, `NotApplicable`, `Superseded`, `Cancelled`, `Faulted` | scheduler/store; cobertura incompleta nunca activa |
| Activation | `Dormant` | `Dormant → Eligible → Activating → Activated` | `Activated`, `NotApplicable`, `Superseded`, `CasRejected`, `RunSuperseded`, `Abandoned`, `Faulted` | observer/store; token o run no vigente supersede |
| CandidateRetirement | `Dormant` | `Dormant → Required → Abandoning → LogicallyAbandoned → Reclaimed` | `Reclaimed`, `NotApplicable`, `Activated` | store; Busy mantiene LogicallyAbandoned y tombstone posee reclaim |
| PreviousViewRetirement | `Dormant` | `Dormant → Required → Retiring → Retired` | `Retired`, `NotApplicable`, `DeferredByLeases`, `Faulted` | store; Retired exactamente una vez tras activación |

Los terminales alternativos pueden alcanzarse desde cualquier estado no terminal solo por el owner indicado y
con causa tipada; no se reabren. Todo fallo posterior a `Accepted` queda en su subestado sin revertir committed,
epoch ni revisión. Para A→B→C sobre una run sin sellar, A y B terminan superseded; al sellar solo C vigente puede
admitirse una vez con `StoreOnly`.

La configuración limita revisiones terminales retenidas y bytes de ledger por inspección. Se reserva antes de
consumir `GatePlanRevision` o token. Si falla, Apply devuelve `CapacityExceeded` sin committed, geometría,
epoch, token, ledger parcial ni `Accepted`. Solo se evictan determinísticamente ledgers completamente terminales
y elegibles. No son elegibles mientras los referencien observer, job, CAS, candidata/tombstone, lease, lifecycle
o diagnóstico publicado pendiente. Dispose reconcilia terminales y recursos antes de liberar store.

Stop normal detiene adquisición, serializa el sellado con el coordinador, publica `FinalRevision`, drena live y
admite únicamente el deferred vigente. No espera todo el job histórico. Dispose cierra editor/coordinador y
drena las operaciones que ya cruzaron su frontera; luego ordena Stop histórico, que cierra admisión y cancela
jobs/backlog; después espera Completion observers y cleanup, reconcilia reservas, candidatas/tombstones y leases,
y finalmente dispone stores/vistas. No espera el backlog antes de ordenarle Stop. Es idempotente.

Apply solo entra en `Running` con admisión abierta; debe satisfacer capacidad, frontera posterior al target y
plan no duplicado. AdmissionClosed, FinalReconciling, Draining, Stopped, Disposing, Closed o Faulted producen
rechazo neutral tipado antes del punto de no retorno. Esta tabla no promete que `Created` o `Draining` sean
observables hoy: primero se caracteriza el lifecycle real. Si Stop gana, Apply no acepta; si Apply cruzó el
punto de no retorno, Stop observa y drena su epoch. Dispose concurrente cierra admisión, conserva la
reconciliación de operaciones aceptadas y puede terminalizar el trabajo pendiente como cancelado/faulted sin
perder Completion, descriptor ni cleanup.

### Anclas e interacción WPF

Application copia anclas exactas a snapshots pequeños e inmutables y libera cualquier lease antes de publicar.
Presentation/WPF nunca reciben store ni leases. Visualization.Core proyecta segundos/umbral sobre la misma
transformación del viewport que A-Scan; nunca usa puntos visuales reducidos como precisión de detector.

Cada `BeginCapture` asigna además un nonce monotónico único. La captura WPF posee una generación ligada, como
mínimo, a ese nonce, `RunId`, `TraceIdentity`, `GatePlanRevision`,
`GateId`, handle, identidad/revisión del anchor y versión de transformación del viewport. Cambiar run, traza,
revisión, gate/handle, anchor, pan, zoom, resize o DPI, o perder captura/foco, ejecuta una única semántica:
invalida la generación, cancela captura, descarta eventos tardíos, restaura el último Preview estable compatible,
no ejecuta Apply y publica un snapshot visual coherente. Nunca mezcla coordenadas de transformaciones distintas.

`AScanControl` recibe una única DependencyProperty de entrada cuyo valor es un snapshot visual inmutable e
indivisible. Contiene `RunId`, `TraceIdentity`, `GatePlanId`, `GatePlanRevision`, GateId/handle
seleccionado, identidad/revisión de anchors, límites/umbral, geometría proyectada, selección, foco lógico, estado
accesible, unidades/rangos, viewport, transformación, DPI, versión de transformación, nonce/generación y estado
Preview/Committed/Conflict/Stale. Render, hit-test, edición, foco, selección y Automation consumen la misma
instancia. Se prohíben bindings separados de revisiones distintas, mezclar geometría anterior con selección o
revisión nueva y conservar captura al cambiar identidad o transformación. Las salidas son intenciones neutrales
con la generación observada; nunca mutan la autoridad.

Los handles son objetivos focalizables con Tab estable y Automation (nombre, valor, unidad, límites,
validación/estado). Flechas usan pasos configurados, Enter confirma cuando procede y Escape cancela/restaura.

## Invariantes

- RF confirmada según ADR 0015 continúa siendo la única autoridad de frontera.
- Una identidad cae exactamente en un epoch live.
- `Accepted` implica committed+epoch, no finalización histórica.
- Fase 6 usa `StoreOnly`; token, candidata y predecesora se comprueban en el CAS del store.
- Cambiar run impide activar candidatas de la run anterior.
- Cada job admitido conserva un observer terminal Application hasta reconciliarlo.
- Preview/Cancel no tienen efectos productivos y Presentation/WPF no poseen autoridad ni leases.
- No hay callbacks bajo exclusión ni estados terminales que se reabran.

## Consecuencias y alternativas

Se necesitan el coordinador Application, puertos preparados de epoch/store/ledger y CAS/abandono en el store.
Se rechazan reutilizar `_gate`, activar desde scheduler mediante `ActivateOnComplete`, invalidar la vista antes
de materializar el reemplazo y hacer check-then-act de token en Application. Outbox, recuperación durable y una
vista activa por run se aplazan.

## Estrategia de pruebas

Las carreras usan barreras/TCS y `TimeProvider`, sin sleeps, polling, hooks productivos ni reflexión sobre
miembros privados. Comprueban estado autoritativo, no solo contadores. Como mínimo:

- RF confirmada pero sesión aún no actualizada; Apply observa N y crea N+1; caso equivalente para gap;
- ambos órdenes Apply/commit RF y ausencia de callbacks bajo coordinador;
- `PrepareEpoch` fallido, exact-limit/one-over, Commit, Abort, doble Commit, Commit tras Abort, doble Abort,
  cancelación/Dispose con reserva viva, pin frente a drain/retirada y Commit sin asignaciones/fallos observables;
- CAS A antes de aceptar B; B instala token antes del CAS A; B captura VA si A ya activó;
- CAS gana antes del Commit de cambio de run y este marca la vista Stale/NotVisible antes de publicar; transición
  gana antes del CAS y el CAS tardío devuelve RunSuperseded; callbacks tardíos no restauran token/visibilidad;
- waiter externo cancelado, Completion posterior consumida por Application, candidata reclamada y Dispose espera;
- fallo/cancelación entre admisión y registro: o no hay admisión o el observer ya posee Completion;
- CAS frente a abandono en ambos órdenes, reclaim `Busy`, tombstone completa y vista activa nunca reclamada;
- Stop del scheduler precede la espera de observers, incluso con backlog y worker bloqueado;
- Stop de run admite job y retorna; Dispose empieza inmediatamente; Stop histórico cancela o deja terminar según
  estado; observer consume Completion; descriptor se activa o reclama y store no se dispone prematuramente;
- Apply antes de Stop; Stop gana antes del punto de no retorno; Apply cruza antes de Stop; Apply contra Dispose;
  ninguna ruta publica Accepted parcial;
- abrir nueva run con vista anterior activa, publicación stale/no-visible, primer CAS/retiro y cleanup completo;
- WPF: cambio de traza/revisión/gate/handle/anchor, pan/zoom/resize/DPI, pérdida de captura/foco y eventos tardíos;
- captura A cancelada y captura B con la misma tupla: el callback tardío de A se descarta por nonce;
- cada estado inicial/transición/terminal del ledger; transición ilegal; terminal no reabierto; Dormant frente a
  NotApplicable; exact-limit/one-over por revisiones y bytes; retención bloqueada por observer, CAS, tombstone,
  lease y lifecycle; evicción determinista; CapacityExceeded no consume revisión/token;
- reservas epoch/store/run: default, doble Commit, Commit tras Abort, doble Abort, Abort tras Commit,
  owner/generación incorrectos y Dispose con reserva Prepared;
- capacidad configurada N: epoch inicial + N−1 Apply aceptados, siguiente Apply rechazado sin revisión/token/
  ledger; Abort devuelve solo el slot provisional y un epoch confirmado no devuelve slot antes de retirar run;
- balances finales de observers, reservas, callbacks, commits, leases, vistas, tombstones y anchors en cero.

## Límites de alcance

No se implementan aún Fase 6, Undo, persistencia durable/outbox, C-Scan, formatos externos, PDF, movimiento,
hardware nuevo ni 3D. Este ADR está **Accepted**; su aceptación no implica que Fase 6 esté implementada.
