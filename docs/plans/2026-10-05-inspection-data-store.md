# Plan por fases del almacén autoritativo de inspección

Estado: Fases 0, 1 y 2 completadas. Fase 3 en curso; validación y gates pendientes.
Fuente normativa: [ADR 0015](../adr/0015-authoritative-inspection-data-store.md).

## Reglas transversales

Una sola autoridad por fase; memoria y colas acotadas por bytes y elementos; ownership explícito;
almacenamiento sin pérdida silenciosa y visualización latest-only; lectura parcial; operaciones largas
cancelables; contratos neutrales; ninguna combinación de máximos se promete sin validación completa.
Cada fase futura debe definir propietarios de archivos, pruebas de integridad/carga/cierre, budgets,
benchmarks reproducibles, revisión de diff y gates antes de avanzar.

## Fase 0 — ADR y plan

Alcance: ADR 0015, este plan y actualización coherente de requisitos, arquitectura, pipeline,
almacenamiento, índice ADR y cuestiones abiertas. Sin código, proyectos, paquetes ni pruebas.

Salida: ADR 0015 Aceptado, decisiones y pendientes diferenciados, cálculos trazables, enlaces válidos
y `git diff --check`. No se crean proyectos ni código en esta fase.

## Fase 1 — Contratos y store acotado en memoria

Crear, solo en esta fase futura, contratos neutrales en `UTStudio.Contracts/Inspection` y el proyecto
`UTStudio.Inspection.Core` para un store append-only exclusivamente en memoria. No crear
`UTStudio.Inspection.Storage`.

`TraceIdentity` contendrá InspectionId, AcquisitionRunId, UtSourceId, PhysicalChannelId, BeamId o
FocalLawId opcionales/tipados y AcquisitionIndex; Sequence permanece separado y nunca sustituye el
índice. Habrá un writer por run y lectores concurrentes.

`AppendAsync` recibe memoria prestada hasta completar: reserva presupuesto antes de copiar/adquirir
ownership y publica commit de traza todo-o-nada. El resultado indica por separado `WasAccepted`
(admitido como writer antes del cierre), `IsCommitted` y `Durable=false`, y conserva estados terminales
`Committed`, `Duplicate`, `Conflict`, `CapacityExceeded`, `Closed`, `Cancelled` o `Faulted`.
`WasAccepted` no es un resultado terminal. Cancelación precommit revierte; postcommit no deshace. Una
identidad confirmada nunca se sobrescribe.

Lectura exacta por identidad y rango por stream+AcquisitionIndex devuelve
Available/NotAvailable/Gap/Cancelled/Faulted. Leases inmutables tienen Dispose idempotente; cierre los
espera; snapshots independientes son opcionales.

`SampleCapacityBudgetBytes` cuenta exactamente reservas y arrays propios de muestras;
`MaximumEntries` acota por separado metadata e índices administrados. No se promete un límite exacto
del heap o working set del CLR. Los diagnósticos separan bytes reservados, materializados, lógicos y
máximo materializado. No se expulsan datos autoritativos. Chunks RAM de tamaño provisional configurable
se sellan por capacidad, cambio incompatible o cierre; no definen formato de archivo y sus índices
solo apuntan a commits confirmados.

Gate: límites demostrados; backpressure o CapacityExceeded sin overwrite/drop; ownership único;
lecturas concurrentes coherentes; ninguna traza parcial; cierre espera leases; Durable=false.

## Fase 2 — Integración ApplicationSession/simulador

Convertir el store en autoridad única; `ApplicationSession` presta memoria solo durante `AppendAsync`
y el store copia o adquiere ownership antes de completarlo.
Retirar el fan-out autoritativo, conservar adaptadores visuales derivados y propagar backpressure/fallo
observable. Simulador y futuras fuentes siguen `IUtFrameSource`.

Gate: cada frame válido queda aceptado o rechazado explícitamente y se libera una vez; gaps y cierre
ordenado cubiertos; no dual-write.

Implementado: `ApplicationSession` valida identidad/cabecera, confirma un gap antes de avanzar el
watermark observado y antes de validar índice/tiempo, espera el commit transaccional de cada traza
admitida y solo después alimenta el fan-out A-Scan/B-Scan como proyección derivada. Un rechazo o fallo del store es
un fallo observable de ingestión, detiene la ejecución y drena/libera los frames. El runtime crea un
`InspectionId` estable y un store en memoria único para la inspección; las runs sucesivas comparten
su presupuesto global y conservan identidades separadas. Cada run tiene un único writer ligado, se
sella idempotentemente tras drenar operaciones admitidas y no puede reabrirse. `GapRecord` conserva
rangos inclusivos de `Sequence` sin inferir `AcquisitionIndex`; cada unidad confirmada y su revisión se
publican atómicamente. El store se cierra después de la sesión y antes de liberar la
fuente. El gate de Fase 2 queda satisfecho por pruebas deterministas de atomicidad, exclusividad,
sellado, cierre, ownership y runs sucesivas. La validación específica y la suite Core completa están
verdes; las tres regresiones B-Scan que bloqueaban la suite se corrigieron únicamente en pruebas.

## Fase 3 — A-Scan sobre IUtTraceProvider

Live consume la última traza confirmada mediante mailbox latest-only; Selected obtiene snapshot por
identidad. Mantener navegación temporal, límites de publicación y cierre del ViewModel.

Gate: coalescencia visual no afecta contadores del store; selección exacta sobre revisión estable.

En curso: la sesión confirma primero y notifica solo identidad+revisión; A-Scan usa un mailbox
latest-only, adquiere un lease de `IUtTraceProvider`, proyecta bajo el lease y publica solo si run y
generación siguen vigentes. El cierre bloquea nuevas notificaciones, cancela candidatos no iniciados y
espera lecturas/callbacks admitidos sin forzar leases. La selección B→A usa el provider autoritativo y
`AcquisitionIndex`. B-Scan conserva temporalmente su proyección derivada; persistencia sigue aplazada.
No se declara completada hasta retirar leases de Presentation, añadir la cobertura determinista exigida
y dejar verdes Core, WPF y las repeticiones de cierre.

## Fase 4 — B-Scan sobre rangos

Consumir páginas/rangos del provider; mantener temporalmente `BScanTraceStore` solo como caché derivada
o adaptador, migrar selección B→A y retirarlo progresivamente. Conservar rango residente vs dirty,
gaps, resync, budgets, leases breves y cierre/reapertura de ventana.

Gate: el store es la única autoridad; `BScanTraceStore` es solo cache/proyección derivada temporal;
selección B→A usa `TraceIdentity`; cerrar B no pierde datos; ventanas 256/1.024 respetan budget.

Decisión confirmada para esta fase: `IInspectionStreamStateProvider`, separado de
`IInspectionRangeProvider`, ofrece un snapshot neutral de estado por stream,
capturado bajo una revisión, con estado de run, revisión final opcional y watermarks dispersos de
`AcquisitionIndex` confirmado. La notificación committed solo despierta/coalesce; el proyector consulta
estado y revisión para reconciliar eventos omitidos. Los gaps de `Sequence` son solo diagnóstico y no
crean columnas. Ausencias interiores continúan `NotAvailable`; `Gap` queda reservado para evidencia
explícita futura por `AcquisitionIndex`; fuera de watermarks finales se devuelve `OutOfRange`.

El resultado usa estados explícitos `Unknown`, `Available`, `NotAvailable`, `Closed`, `Cancelled` y
`Faulted`, con factorías que impiden combinaciones inválidas de snapshot y código tipado. El valor
`default` es `Unknown`. La cancelación se decide dentro de la puerta del store antes de capturar; una
captura ya linealizada permanece válida. `Snapshot.RunState=Faulted` describe lifecycle y no debe
confundirse con `Status=Faulted`, que describe un fallo de la consulta.

## Fase 5 — Persistencia incremental

Implementar chunks, commits, journal, versiones, checksum e índice reconstruible tras aprobar las
decisiones tecnológicas necesarias. Inyectar disco lento/lleno, truncamientos y corrupción. Medir
append/commit/flush, capacidad de absorción y recuperación.

Gate: ningún dato aceptado desaparece silenciosamente; admisión, commit y durabilidad verificables;
recovery en cada frontera y cierre bajo presión.

## Fase 6 — Lectura desde archivo

Abrir inspecciones grandes sin carga completa; cache hot/cold, consultas exactas y parciales, paginación,
replay controlable e importadores separados. No confundir reader con `IUtFrameSource`: un adaptador de
reproducción implementa la fuente común sobre el reader.

Gate: 15 y 29 GB sintéticos generados fuera de Git; RAM estable; índice reconstruible; errores y run
incompleta observables.

## Fase 7 — C-Scan en otro incremento

Definir requisitos de encoder, geometría, PA, gates, tiles, multirresolución y rendimiento antes de
elegir contratos visuales. Derivar desde `TraceIdentity` + `InspectionRevision`; no cambiar la autoridad.

Gate: ADR y plan propios aprobados. Esta fase no está autorizada por el presente documento.

## Riesgos priorizados

- P1: ownership ambiguo, dos autoridades, drop silencioso, commit parcial, cierre falso y backpressure
  incompatible con hardware.
- P2: identidad insuficiente, leases largos, revisión ambigua, índice no reconstruible, consultas densas
  y doble conteo de memoria.
- P3: optimizar prematuramente chunks/layout, elegir formato/compresión/checksum/mmap sin evidencia o
  generalizar C-Scan antes de encoder, PA y gates.

## Decisiones previas a fases afectadas

Antes de Fase 5: formato, tamaño de chunk persistente, compresión, checksum, durabilidad y política ante disco lento.
Antes de PA/rectificada: grouping/layout, Sequence y encoding. Antes de Fase 7: encoder, coordenadas,
gates y objetivos medidos. Ninguna queda resuelta por este plan.
