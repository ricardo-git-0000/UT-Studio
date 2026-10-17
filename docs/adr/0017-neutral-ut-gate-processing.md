# ADR 0017 — Procesamiento neutral de puertas UT

Fecha: 2026-10-16. Estado: **Aceptado**.
Trazabilidad: R01, R02, R04-R08, R10 y R13; complementa [ADR 0007](0007-frame-source-ownership.md),
[ADR 0008](0008-latest-only-visual-delivery.md), [ADR 0010](0010-ascan-manual-cursor-measurements.md),
[ADR 0015](0015-authoritative-inspection-data-store.md) y
[ADR 0016](0016-reusable-complete-scan-components.md).
Diseño detallado: [procesamiento de puertas UT](../architecture/ut-gate-processing-design.md).
Ejecución: [plan por fases](../plans/2026-10-16-ut-gate-processing.md).

## Contexto

El producto necesita evaluar una traza UT confirmada con un plan de al menos tres puertas independientes
o ligadas. El resultado debe ser determinista, acotado, reutilizable y reconstruible. No puede depender
de WPF, vistas A/C-Scan, hardware, simulador, `ApplicationSession`, una implementación concreta del store
ni posición espacial.

`InspectionDataStore` continúa siendo la única autoridad RF. La entrega visual vigente es `latest-only`
y puede coalescer; por tanto no constituye un transporte lossless para procesamiento. Los cursores A/B
son medidas manuales visuales aproximadas y no son puertas.

## Decisión

Se adopta una nueva unidad neutral `UTStudio.Processing.Core`, sin referencia WPF, que implemente:

- `GatePlanCompiler`, puro y thread-safe;
- `GateEvaluator`, reentrante y sin estado semántico mutable; únicamente puede compartir scratch acotado;
- detectores y transformaciones compartidas;
- resultados y diagnósticos acotados.

Los contratos compartidos se ubican en `UTStudio.Contracts/Processing`; los value objects de identidad y
revisión se ubican en `UTStudio.Domain/Processing`. `Contracts` depende de `Domain`, y ninguno depende de
`Processing.Core`.

El flujo base introducido en Fase 3 es:

`commit RF -> aviso sample-free acotado -> reconciliación por InspectionRevision -> lease IUtTraceProvider
-> evaluación -> sink esperado -> liberar lease`. Fase 4 reemplaza el sink transitorio productivo por el
commit atómico en el store derivado RAM, manteniendo el mismo contrato de batch prestado.

El puerto de ingreso de procesamiento será distinto de `ICommittedTraceSink`. Su saturación será
observable y nunca usará `DropOldest`, coalescing de trabajo lógico ni fallback silencioso; los avisos
sample-free sí pueden coalescerse en un watermark porque el catálogo conserva todas las identidades. Una cola no constituye por sí
sola una garantía lossless: el scheduler deberá reconciliar identidades/revisiones confirmadas contra la
autoridad —y `FinalRevision` al sellar— o usar un outbox/ledger futuro. Un fallo derivado no revierte el
commit RF.

La implementación usa un catálogo paginado de commits (`Trace` o `GapOnly`) como extensión necesaria del
store autoritativo. La señal solo eleva un watermark coalescido: pérdida, duplicación, desorden, llegada tardía
o saturación no elimina trabajo lógico, y el sellado obliga a reconciliar hasta `FinalRevision`. El punto de
admisión es `OpenRun`; después de aceptarlo, Stop drena la run. El estado queda acotado por runs, streams,
épocas de plan y tamaño de página, no por número de trazas.

Cada plan será un snapshot inmutable identificado y versionado. El compilador rechazará IDs duplicados,
referencias ausentes, autorreferencias, ciclos, parámetros inválidos y combinaciones no soportadas; emitirá
un orden topológico determinista con el orden declarado como desempate. Una dependencia solo se satisface
con `Detected`; cualquier otro estado produce `DependencyNotDetected`, conservando puerta y estado causal.

La primera versión usa `inicio referenciado + duración positiva`, con inicio referido al origen temporal de
la traza o al evento detectado por una puerta más un offset. La ventana completa debe quedar dentro de la
traza. Referenciar inicio y fin independientemente queda fuera del primer incremento.

El evaluator recibe descriptor, vista neutral de muestras, plan compilado y cancelación. No conserva RF,
no publica UI, no asigna por muestra y devuelve un lote completo por traza. El lease se retiene solo durante
la evaluación y se libera en `finally`. Planes compilados son compartibles; scratch por llamada se obtiene
de un presupuesto acotado mediante espera cancelable o rechazo contabilizado, sin allocation de fallback, y
se devuelve en `finally`. La cancelación se observa en fronteras deterministas; descarta intermedios y produce
un batch terminal completo `Cancelled` confirmado atómicamente, nunca un lote parcial.

Los fallos conservan un error principal y errores secundarios de cleanup ordenados. Una cancelación o excepción
original nunca es sustituida por un fallo de devolución de scratch; si solo falla la devolución, ese fallo es el
principal.

La firma completa de reproducibilidad contiene `TraceIdentity`, `SourceInspectionRevision`, `GatePlanId`,
`GatePlanRevision`, `GateId`, `GateRevision`, `ProcessingContractVersion`, versión del algoritmo, descriptor/
eje efectivo y dominio/parámetros efectivos. Cambiar cualquier semántica de puerta incrementa `GateRevision`;
cambiar contenido, topología o política incrementa la revisión del plan. Los resultados anteriores no mutan.

En Fase 2 `ProcessingContractVersion=3`. El descriptor DSP efectivo firmado comprende identidad confirmada,
revisión fuente, número de muestras, frecuencia, offset de primera muestra, encoding, calidad efectiva y
procedencia. Secuencia, tiempo desde inicio de run, timestamp, calidad de reloj y coordenada espacial se excluyen
porque no alteran muestras, eje temporal, ventanas ni detectores; su variación no cambia la firma.

La asociación espacial ocurre después mediante `TraceIdentity`. Las capas iniciales son amplitud, tiempo de
detección, TOF, estado y calidad. Solo una medida válida aporta su valor; estados y calidad conservan máscara,
causa y procedencia, nunca cero o `NaN` implícitos. Este ADR no diseña geometría, celdas, tiles ni renderizado
C-Scan.

Los dominios iniciales son `RawSigned`, `Positive`, `NegativeMagnitude` y `Absolute`. La señal rectificada
real se aplaza hasta resolver su encoding persistente. Los cruces usan interpolación lineal y la muestra
izquierda como `SampleIndex`, armado explícito y `NoEvent` cuando no hay flanco. Los máximos locales excluyen
bordes y representan una meseta por su centro inferior. El tiempo canónico se mide desde el origen de traza.
`TraceQuality.Degraded` es procesable y degrada la calidad del resultado; `Invalid` no es procesable.

La configuración predeterminada admite 8 puertas y el máximo contractual es 16. El primer incremento no
incorpora `ElementId`. Stop cierra admisión y drena todo trabajo admitido; no cancela backlog admitido como
política normal. El store derivado inicial es en memoria, acotado y reconstruible mientras la RF autoritativa
esté disponible. A-Scan edita mediante Preview y confirmación `Apply` o descarte `Cancel`.

Precisión de Fase 1: el compilador neutral acepta planes de 1 a su máximo configurado para permitir composición
y pruebas incrementales. El máximo configurado predeterminado es 8 y nunca puede superar 16. El requisito de
producto de tres puertas describe la capacidad/caso mínimo que debe demostrarse, no una prohibición contractual
de compilar un plan de una o dos puertas. Esta precisión, junto con la ubicación de identidades y el desempate
por orden declarado, prevalece sobre cualquier redacción incompatible del diseño detallado anterior.

Precisión de Fase 2: el esquema y algoritmo canónicos pasan a versión 2 al retirar la política por puerta
`GoodOnly`. La calidad efectiva confirmada es la única autoridad: `Good` y `Degraded` se procesan, mientras
`Invalid` produce `InvalidTrace` sin leer muestras. El descriptor confirmado conserva el `TraceDescriptor`,
la identidad confirmada y una `SourceInspectionRevision` no inicial. Los umbrales normalizados `u` se convierten
en `double`, sin redondeo ni clamp: `RawSigned=-32768+65535u`, `Positive=32767u` y
`NegativeMagnitude/Absolute=32768u`. El TOF publicado es absoluto respecto al origen de la traza; la referencia
causal de una puerta ligada se conserva por separado.

## Refinamiento aceptado de Fase 4

El store derivado inicial es RAM, acotado, reconstruible y no autoritativo. `DerivedStoreRevision` es
monótona por instancia y avanza una vez por cada commit nuevo, activación, invalidación lógica o reclamación
física confirmada. No avanza por duplicado, conflicto, saturación, cancelación previa, rollback, consulta ni
liberación de lease. Cada mutación devuelve la revisión que la confirmó.

La unidad indivisible es un `GateEvaluationBatch` completo. Su clave contiene `TraceIdentity`, revisión RF,
plan/revisión/firma, esquema y algoritmo; la clave de capa añade puerta/revisión, medida, unidad y dominio.
Un duplicado equivalente es idempotente, una clave con contenido diferente es conflicto y nunca se sobrescribe.

La cobertura se declara mediante un manifest cerrado y disperso producido por el ledger terminal de Fase 3:
streams explícitos, watermarks opcionales, recuento exacto y SHA-256 canónico de identidades ordenadas,
`SourceFinalRevision`, epoch completo y `CapturedStoreRevision`, la revisión exacta del store usada para formar
la evidencia. Activar materializa una membresía inmutable de claves confirmadas hasta esa revisión y los leases
leen exclusivamente esa membresía. Al drenar, el orquestador publica en una
sola operación atómica la revisión terminal RF y la cobertura completa de identidades elegibles de todos los
epochs; claves terminales repetidas o un lote que mezcle run/revisión final son conflicto sin publicación. Activar
exige igualdad exacta. No se inventan identidades entre
watermarks; una run vacía es válida con digest del conjunto vacío. La activación linealizable solo cambia la
vista cuando recuentos, digest, revisión final, streams y epoch coinciden por completo.

Un lease fija una revisión, descriptor de vista y epoch. Las consultas exactas y regionales usan páginas de
1..256, intervalos inclusivos de `AcquisitionIndex` y cursores opacos ligados a consulta, vista, revisión y
lease. Ausencias se omiten o se expresan como `NoData`, nunca como cero.

La retención inicial es `KeepUntilInvalidatedOrClose`: sin LRU ni expulsión implícita. Invalidar la vista
activa la deja sin vista; `ReclaimInvalidatedAsync` es explícito y devuelve `Busy` si un lease captura contenido
afectado. El presupuesto usa límites independientes de batches y bytes lógicos mediante fórmula pública,
checked y versionada; su versión 2 incluye cardinalidad de medidas, índices, membresías exactas, batches,
manifests, evidencia terminal, vistas y el tombstone permanente de cada
`DerivedViewId`. Reserva antes de copiar y revierte en rollback. La saturación es inmediata y no muta el store.

`DisposeAsync` bloquea admisión y espera writers, lectores en construcción y leases admitidos, sin liberarlos
por fuerza. El store copia/proyecta el batch prestado por el sink antes de responder y nunca conserva RF ni
referencias al almacenamiento del evaluator. Persistencia, reprocesado general, UI y geometría C-Scan siguen
fuera de Fase 4.

## Consecuencias

- La autoridad RF, identidades y leases de ADR 0015 se conservan.
- Procesamiento tiene backpressure y cierre propios; Stop cierra admisión y drena todo lo admitido antes de
  cerrar el store.
- A-Scan podrá editar y mostrar un plan prestado, pero no poseerlo ni evaluarlo.
- Reprocesar usa RF almacenada y una revisión concreta del plan; no requiere hardware ni UI.
- Rectificada real y envolvente permanecen no soportadas hasta decisiones posteriores.
- Las Fases 1 y 2 añaden contratos, compilador y evaluación síncrona neutral de una traza; no añaden pipeline,
  store derivado, UI, persistencia ni dependencia NuGet.

## Alternativas descartadas por ahora

- Evaluar antes del commit: rompe la autoridad y ownership establecidos por ADR 0007/0015.
- Reutilizar la entrega visual: puede perder/coalescer trabajo y no expresa backpressure.
- Guardar RF en Processing: crea una segunda autoridad.
- Hacer ambos extremos de ventana referenciables desde la primera versión: añade dos dependencias, ventanas
  dinámicamente invertidas y fallos parciales sin un caso confirmado.
- Integrar geometría/C-Scan en el evaluator: mezcla señal y posicionamiento.

## Cuestiones separadas

Q19 queda resuelta para el primer incremento. Permanecen separadas únicamente el encoding persistente de
señal rectificada, el presupuesto final después de benchmarks, la persistencia del store derivado y los
requisitos PA reales. Ninguna reabre las quince decisiones de este ADR.
