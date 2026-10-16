# ADR 0017 — Procesamiento neutral de puertas UT

Fecha: 2026-10-16. Estado: **propuesto; pendiente de aprobación**.
Trazabilidad: R01, R02, R04-R08, R10 y R13; complementa [ADR 0007](0007-frame-source-ownership.md),
[ADR 0008](0008-latest-only-visual-delivery.md), [ADR 0010](0010-ascan-manual-cursor-measurements.md),
[ADR 0015](0015-authoritative-inspection-data-store.md) y
[ADR 0016](0016-reusable-complete-scan-components.md).
Diseño detallado: [procesamiento de puertas UT](../architecture/ut-gate-processing-design.md).
Ejecución propuesta: [plan por fases](../plans/2026-10-16-ut-gate-processing.md).

## Contexto

El producto necesita evaluar una traza UT confirmada con un plan de al menos tres puertas independientes
o ligadas. El resultado debe ser determinista, acotado, reutilizable y reconstruible. No puede depender
de WPF, vistas A/C-Scan, hardware, simulador, `ApplicationSession`, una implementación concreta del store
ni posición espacial.

`InspectionDataStore` continúa siendo la única autoridad RF. La entrega visual vigente es `latest-only`
y puede coalescer; por tanto no constituye un transporte lossless para procesamiento. Los cursores A/B
son medidas manuales visuales aproximadas y no son puertas.

## Decisión propuesta

Se propone una nueva unidad neutral `UTStudio.Processing.Core`, sin referencia WPF, que implemente:

- `GatePlanCompiler`, puro y thread-safe;
- `GateEvaluator`, reentrante y sin estado mutable compartido;
- detectores y transformaciones compartidas;
- resultados y diagnósticos acotados.

Los contratos compartidos se ubicarían en `UTStudio.Contracts/Processing`; los value objects de identidad
solo pasarían a `UTStudio.Domain` si se confirma que son vocabulario transversal estable. `Contracts` no
dependerá de `Processing.Core`. Esta ubicación exacta queda pendiente de aprobación.

El flujo futuro será:

`commit RF -> aviso sample-free acotado y lossless -> lease IUtTraceProvider -> evaluación -> liberar lease
-> commit derivado atómico -> notificación a consumidores`.

El puerto de ingreso de procesamiento será distinto de `ICommittedTraceSink`. Su saturación será
observable y nunca usará `DropOldest`, coalescing ni fallback silencioso. Una cola no constituye por sí
sola una garantía lossless: el scheduler deberá reconciliar identidades/revisiones confirmadas contra la
autoridad —y `FinalRevision` al sellar— o usar un outbox/ledger futuro. Un fallo derivado no revierte el
commit RF.

Cada plan será un snapshot inmutable identificado y versionado. El compilador rechazará IDs duplicados,
referencias ausentes, autorreferencias, ciclos, parámetros inválidos y combinaciones no soportadas; emitirá
un orden topológico determinista con `GateId` canónico como desempate. Una dependencia solo se satisface
con `Detected`; cualquier otro estado produce `DependencyNotDetected`, conservando puerta y estado causal.

Para la primera versión se propone `inicio referenciado + duración positiva`, con inicio referido al origen
temporal de la traza o al evento detectado por una puerta más un offset. Referenciar inicio y fin de forma
independiente queda reservado hasta que exista un caso UT confirmado.

El evaluator recibe descriptor, vista neutral de muestras, plan compilado y cancelación. No conserva RF,
no publica UI, no asigna por muestra y devuelve un lote completo por traza. El lease se retiene solo durante
la evaluación y se libera en `finally`. Planes compilados son compartibles; scratch por llamada se obtiene
de un presupuesto acotado mediante espera cancelable o rechazo contabilizado, sin allocation de fallback, y
se devuelve en `finally`. La cancelación se observa en fronteras deterministas; descarta intermedios y produce
un batch terminal completo `Cancelled` confirmado atómicamente, nunca un lote parcial.

Los derivados se identifican al menos por `TraceIdentity`, plan/revisión, `GateId`, `GateRevision`, versión
del contrato/algoritmo y `InspectionRevision` fuente. Cambiar cualquier semántica de puerta incrementa
`GateRevision`; cambiar contenido, topología o política incrementa la revisión del plan. Los resultados
anteriores no mutan: una nueva generación sustituye la vista activa y queda sujeta a retención/GC explícitos.

La asociación espacial ocurre después mediante `TraceIdentity`. Una medida válida puede generar una capa
C-Scan independiente —por ejemplo amplitud y TOF—, pero solo `Detected` aporta valor; el resto aporta
`NoData` con causa y procedencia, nunca cero o `NaN` sin máscara. Este ADR no diseña geometría, celdas,
tiles ni renderizado C-Scan.

## Consecuencias

- La autoridad RF, identidades y leases de ADR 0015 se conservan.
- Procesamiento tiene backpressure y cierre propios; Stop debe cerrar admisión y drenar o cancelar de forma
  contabilizada antes de cerrar el store.
- A-Scan podrá editar y mostrar un plan prestado, pero no poseerlo ni evaluarlo.
- Reprocesar usa RF almacenada y una revisión concreta del plan; no requiere hardware ni UI.
- Rectificada adquirida y envolvente permanecen no soportadas hasta definir encoding/calibración y algoritmo.
- No se crea código, proyecto, UI, persistencia ni dependencia NuGet en esta fase documental.

## Alternativas descartadas por ahora

- Evaluar antes del commit: rompe la autoridad y ownership establecidos por ADR 0007/0015.
- Reutilizar la entrega visual: puede perder/coalescer trabajo y no expresa backpressure.
- Guardar RF en Processing: crea una segunda autoridad.
- Hacer ambos extremos de ventana referenciables desde la primera versión: añade dos dependencias, ventanas
  dinámicamente invertidas y fallos parciales sin un caso confirmado.
- Integrar geometría/C-Scan en el evaluator: mezcla señal y posicionamiento.

## Aprobación necesaria

Este ADR no está aceptado. Las elecciones enumeradas en el diseño detallado —dominios, ventana parcial,
cruces, máximos locales, TOF, revisiones, calidad, backpressure, límites y retención— requieren decisión
del usuario antes de implementar.
