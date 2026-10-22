# Fase 6 B8B — composición productiva del editor de gates

Fecha: 2026-10-22.

## Objetivo

Sustituir el grafo legacy de `DesktopRuntime` por el scope productivo de B8A y conectar, por vista, edición,
Preview exacto, geometría, interacción WPF y Apply explícito sin duplicar autoridad, compilación ni lifecycle.

## Resultado

- `DesktopRuntime` crea un único `ProductiveGateInspectionScope`; la sesión y el contexto A-Scan toman prestados
  sus puertos. Las instancias registradas en DI no crean owners alternativos y el runtime dispone el scope una
  sola vez antes de cerrar los stores de inspección.
- `GateEditorViewCoordinator` posee exclusivamente el draft, el overlay y el trabajo visual de una vista. La
  autoridad, Apply, evaluator y compilador son prestados. El cierre de vista drena evaluación/publicación visual;
  un Apply admitido continúa bajo Application y su observer usa referencia débil, sin retener la vista cerrada.
- Preview y Apply comparten el mismo `CompiledGatePlan`. El ticket de Apply incluye identidad Preview, referencia
  exacta al artefacto y generación editorial; una respuesta antigua no sustituye una edición posterior.
- La convergencia admite ambos órdenes: publicación de autoridad antes del terminal Accepted y Accepted antes de
  que la vista procese autoridad. Presentation nunca instala autoridad.
- Cancel de edición, cancelación del waiter y cierre de vista permanecen separados. Tras admisión ninguna de esas
  acciones revierte Apply ni detiene su reconciliación productiva.
- Saturación y cierre preadmisión se muestran desde `GatePlanApplySubmissionStatus`; conflictos, capacidad,
  cierre, stale, validación y fallo terminal conservan estados visibles sin fabricar `GatePlanEditResult`.
- `AScanView` conecta las intenciones del control con las máquinas portables ya existentes. BeginCapture valida
  la misma instancia visual mostrada y admite publicación reentrante. Enter confirma Preview y nunca ejecuta
  Apply. La toolbar mínima expone Editar, Preview, Cancelar y Aplicar mediante comandos accesibles.
- El fallback `NotRequested` queda reservado a contextos visual-only. Con contexto productivo se conserva el
  estado explícito del overlay; una señal legítima sin identidad sigue visible pero no habilita edición.

## Ownership y cierre

El scope posee autoridad, live, store derivado, Apply, scheduler histórico, evaluator y budgets. La sesión es
propiedad del scope; las vistas no disponen ningún owner inspection-scoped. El cierre de escritorio bloquea
interacción y ventanas, cierra A-Scan y sus evaluaciones, dispone el scope para parar/drenar sesión, Apply,
handoffs y observers, después detiene y drena deliveries A/B-Scan y finalmente cierra stores y fuente. No se
mantienen gates durante esperas.

## Pruebas y validaciones

- Focalizadas Core de overlay, editor y scope tras el diagnóstico: 31/31.
- Focalizadas WPF de composición y vista A-Scan tras el diagnóstico: 25/25.
- Core completa Release: 1065/1065.
- WPF completa Release: 140/140.
- Build Release: correcto, 0 warnings y 0 errores.
- `dotnet format UTStudio.sln --no-restore --verify-no-changes`: correcto tras aplicar el formato mecánico
  detectado en los archivos de B8B.
- `git diff --check`: correcto.
- UTF-8, enlaces locales y dependencias de proyecto: correctos; Presentation permanece independiente de WPF y
  Application no depende del store derivado concreto desde la composición Desktop.

La aceptación vertical incluye una prueba STA determinista sobre la composición real de `DesktopRuntime`. Una
fuente controlada mediante `Channel` y `TaskCompletionSource` gobierna adquisición, devolución del frame,
habilitación de comandos, publicación de autoridad y cierre, sin `sleep` ni polling. El recorrido comprobado es
adquisición → Editar → Preview exacto → Apply → committed → Stop/sellado → handoff histórico → cierre repetido.
Apply se reconcilia aunque se cancele la espera visual; Stop retorna sin esperar indebidamente la Completion
histórica y el shutdown compartido drena los owners que realmente posee.

El timeout experimental quedó localizado antes del drenaje de Host: al converger Apply a committed, la
publicación del overlay reentraba por coordinador, ViewModel y vista; `SetVisualContext` recibía el mismo contexto,
pero el coordinador solicitaba otra evaluación committed de forma incondicional. La cola acotaba la profundidad
de pila, no el número de publicaciones, y nunca alcanzaba quiescencia. `SetVisualContext` informa ahora si admitió
un cambio real y solo ese cambio puede iniciar la nueva evaluación. Una regresión Core comprueba que el contexto
idéntico no publica ni evalúa de nuevo, y la aceptación vertical completa demuestra que Dispatcher y cierre
progresan. Durante esa aceptación también se corrigieron dos incoherencias reveladas por el wiring real: la traza
adquirida se describe con calidad autoritativa válida y el terminal inmediato de Apply se observa fuera de la
reentrada del comando, conservando ambos órdenes autoridad/Completion.

## Incidencias separadas y límites

- Siguen abiertas, sin causa atribuida ni declaración de resolución, las incidencias lifecycle paralelas y el
  timeout de cleanup B-Scan documentados en B7A/B8A. Las ejecuciones correctas de este bloque no los resuelven.
- Permanece la limitación física de DPI de B7B: el host STA valida transición, invalidación y rectángulo WPF, pero
  no una conversión observada en un monitor físico real.
- El diagnóstico de B8B no establece relación causal con las incidencias lifecycle/B-Scan previas.
- Persistencia y C-Scan no forman parte de la Fase 6.

## Cierre de la auditoría formal

La ampliación de aceptación usa el grafo real de `DesktopRuntime` y seams `internal` exclusivos de prueba para
detener fronteras sin sustituir scheduler, store, FIFO, autoridad ni observers:

- `Productive_gate_vertical_apply_stop_and_close_is_owned_and_repeatable` observa la admisión `StoreOnly` de la
  revisión vigente, Completion, CAS `Activated`, ledger terminal con `PendingResources.None`, un único view
  activo legítimo y cero jobs, observers, candidatas provisionales, tombstones, leases, pins y reservas. El
  snapshot se captura antes de cerrar autoridad; la ausencia posterior de ledgers no se usa como balance.
- `Productive_historical_candidate_is_abandoned_when_a_new_run_supersedes_CAS` bloquea determinísticamente la
  frontera CAS, instala una nueva run y comprueba `RunSuperseded`, abandono/reclaim y balances finales.
  La ejecución conjunta reveló y corrigió que esa ruta dejaba `PreviousViewRetirement=Dormant`; el terminal
  supersedido ahora lo marca `NotApplicable`, por lo que `Dispose` puede demostrar el cierre material.
- `Closing_view_after_apply_admission_does_not_cancel_productive_reconciliation` retiene una RF real en el FIFO,
  confirma que Apply queda admitido sin terminal, cierra la vista, libera RF y comprueba reconciliación posterior,
  ninguna publicación visual tardía, cierre repetido compartido y ausencia final de owners pendientes.
- `Productive_FIFO_saturation_is_visible_and_recovers_without_consuming_a_fake_result` llena la capacidad real
  detrás de una RF retenida, observa Busy sin `GatePlanEditResult` inventado y verifica recuperación.
- `Productive_scope_lends_two_views_independent_preview_identities` crea dos editores contra los puertos del mismo
  scope real y verifica checkpoints y `PreviewIdentity` independientes.

La matriz productiva restante queda demostrada por la combinación del scope real y las regresiones de la misma
semántica en sus fronteras: `CancelledWaiterStillOwnsAdmittedConflictTerminal`; terminal inmediato en la
aceptación vertical principal; `AcceptedApplyDoesNotReplaceANewerEdit`; y
`AcceptedApplyConvergesForBothAuthorityAndCompletionOrders`. Ninguna de estas pruebas reemplaza el mecanismo
productivo de autoridad, compilación, FIFO o lifecycle.

Validación posterior a la auditoría: una ejecución Core reprodujo el timeout de cleanup conocido en
`NewRevisionImmediatelyBeforeCandidateValidationRejectsStaleRaster`; no se atribuye causa ni se declara resuelto.
La repetición Core completa posterior pasó 1065/1065 y WPF completa Release pasó 144/144. Las
comprobaciones manuales pendientes son la operación de teclado/ratón con un lector de pantalla real y la
transición DPI en monitores físicos distintos; no se declaran ejecutadas. La limitación física de DPI y las
incidencias lifecycle/B-Scan continúan separadas y no se declaran resueltas por estas ejecuciones verdes.

## Archivos

Cambian composición DesktopRuntime, scope Application, contexto/ViewModel/editor/overlay Presentation, agregado
visual neutral, adaptador/vista A-Scan WPF, pruebas Core/WPF, este informe, el plan y el estado de arquitectura.
