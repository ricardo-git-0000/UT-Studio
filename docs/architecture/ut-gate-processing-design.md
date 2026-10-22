# Diseño aceptado del procesamiento neutral de puertas UT

Fecha: 2026-10-19. Estado: **contrato aceptado; Fases 1–5 integradas en `main`; Fase 6 implementada en
`feature/ut-gate-ascan-editor`, con B8B validado y pendiente de punto de control; persistencia y C-Scan
siguen fuera**. La
integración de Fase 5 finalizó en `eb3eb2e` mediante `aee0bc1` → `91091a1` → `f29748a` → `eb3eb2e`; la antigua
rama `feature/ut-gate-reprocessing` fue eliminada. El árbol estaba limpio en la comprobación previa a esta
actualización.

## 1. Alcance y autoridad

Requisitos confirmados por la petición vigente:

- procesar una traza confirmada con un plan de una puerta como mínimo y hasta el máximo configurado,
  con puertas independientes o ligadas;
- neutralidad respecto a WPF, A/C-Scan, hardware, simulador, `ApplicationSession`, store concreto y posición;
- determinismo, memoria/colas acotadas, cancelación y resultados versionados;
- `InspectionDataStore` conserva la autoridad RF; resultados de puertas son derivados reconstruibles;
- Fase 2 implementa exclusivamente evaluación síncrona neutral de una traza; pipeline, stores, UI, C-Scan,
  persistencia y paquetes nuevos quedan fuera de Fase 2; Fase 3 incorporó la orquestación post-commit y
  Fase 4 sustituye su consumo transitorio por el store derivado RAM.

Las quince decisiones del apartado 15 están aceptadas para el primer incremento.

## 2. Fronteras y dependencias

Grafo aceptado:

```text
UTStudio.Domain <- UTStudio.Contracts <- UTStudio.Processing.Core
                         ^                       ^
                         |                       |
        Application/orquestador        compiler + evaluator
                         |
         stores derivados / Presentation / visualización
```

`UTStudio.Processing.Core` contiene el compilador/evaluator neutral y, en una unidad separada sin RF, la
implementación RAM del store derivado; no conoce sesión, UI ni posición. Las
identidades y revisiones estables viven en `UTStudio.Domain/Processing`; los contratos públicos compartidos,
en `UTStudio.Contracts/Processing`. `Contracts` depende de `Domain`, y ninguno depende de `Processing.Core`.

Propietario de los documentos de esta fase: agente principal. Revisores especializados: solo lectura.

### Orquestación productiva de Fase 3

`ApplicationSession` valida el frame y confirma RF y gaps atómicamente. Solo si el resultado es `Committed`
envía `TraceIdentity + InspectionRevision` al puerto de procesamiento y después notifica A-Scan y B-Scan.
La señal no contiene muestras, es un aviso de progreso y nunca es autoridad. Rechazo, cierre o fallo del
procesador se registra como error de rama y no revierte el commit RF.

El scheduler conserva por run únicamente cursor, revisión objetivo, streams y épocas de plan: no encola una
entrada por traza. Su presupuesto predeterminado es dos runs, ocho streams y ocho épocas por run, más páginas
de 256 cambios; los máximos se validan al construir. Una run o época que excede capacidad se rechaza con estado
tipado y saturación contabilizada. Las señales aceptadas se coalescen en el watermark y no bloquean esperando
al worker. Duplicadas, tardías o desordenadas no provocan doble evaluación.

La reconciliación consulta el catálogo autoritativo en el intervalo `(cursor, objetivo]`, sin equiparar
`Sequence`, `AcquisitionIndex` e `InspectionRevision`. Un cambio `GapOnly` avanza el cursor sin fabricar una
traza. Para `Trace`, Processing.Core hace una lectura exacta por identidad, valida identidad y revisión, copia
las muestras dentro de la llamada, evalúa el `CompiledGatePlan` de la época fijada y espera el callback del sink.
El lease se libera en `finally`. `NotAvailable` provisional conserva el cursor y se reintenta con la próxima
señal o con `NotifyProviderProgress`; este aviso de disponibilidad evita polling y delays arbitrarios. Los demás
estados permanecen diferenciados y diagnosticados.

Al cambiar de plan, el nuevo snapshot entra en vigor desde una revisión explícita; el trabajo anterior mantiene
su plan y no se generan batches híbridos. No hay reprocesado histórico: un rango sin época válida se rechaza y
queda diagnosticado para Fase 5.

El cierre normal detiene la fuente, drena frames y commits, sella la run, publica `FinalRevision`, cierra la
admisión y espera cursor final, evaluaciones, callbacks y leases antes de cerrar las proyecciones. La cancelación
del worker se reserva para `Dispose` excepcional. El contrato de sink introducido en Fase 3 recibe batches
inmutables prestados, en orden de revisión y mediante callback esperado. En Fase 4 el store copia/proyecta el
batch antes de completar el callback; no hay durabilidad ni persistencia.

El diagnóstico es un snapshot inmutable por run, nunca una selección global de plan. Expone `RunId`, lifecycle,
revisión y firma del plan activo, revisiones objetivo/observada/reconciliada/final, finalidad confirmada,
watermarks por stream, balances por época, trabajo admitido/terminal/pendiente, recursos activos y último error
estructurado. Las runs retiradas pasan a un FIFO terminal acotado (ocho por defecto). Las lecturas concurrentes
se construyen bajo el mismo lock, de modo que no combinan campos de runs o épocas distintas.

Por cada run y plan se exige `ConfirmedEligible = EvaluatedAndDelivered + ExplicitlyRejectedBeforeAdmission +
TerminallyFailed + ExplicitlyUnprocessedByPlanTransition`. `Drained` requiere además `FinalRevision`
reconciliada y saldo cero de workers, callbacks y leases. Gaps, índices ausentes, señales y revisiones
coalescidas no son identidades elegibles ni resultados terminales.

## 3. Modelo inmutable y versionado

API conceptual, no firma C# definitiva:

```text
GateId                    value object no-default, identidad ordinal
GateRevision              revisión monotónica, no reutilizable por GateId
GatePlanId                identidad estable del plan
GatePlanRevision          revisión de contenido, topología y políticas
GateSchemaVersion         versión del esquema del plan
GateAlgorithmVersion      versión de la semántica algorítmica

GatePlan
  Id, Revision, SchemaVersion, AlgorithmVersion
  Gates: immutable ordered collection<GateDefinition>

GateDefinition
  GateId, GateRevision, SignalDomain, DetectorDefinition
  WindowDefinition, RequestedMeasures, AcceptanceCriteria

WindowDefinition                         [primera versión]
  StartReference = TraceOrigin | GateEvent(GateId)
  StartOffsetSeconds: finite double
  DurationSeconds: finite double > 0

GatePlanCompiler.Compile(GatePlan, CancellationToken) -> GatePlanCompilationResult
GateEvaluator.Evaluate(CompiledGatePlan, ConfirmedGateTraceDescriptor,
                       ReadOnlySpan<short>, CancellationToken) -> GateEvaluationResult
```

Colecciones se copian al construir; no se usan diccionarios `string/object`. Cada detector tiene parámetros
tipados. Segundos son `double` finitos; amplitud inicial, cuentas digitales. El porcentaje visual del A-Scan
no es unidad de procesamiento.

El modelo canónico de Fase 2 usa `GateSchemaVersion=2` y `GateAlgorithmVersion=2`. No existe una política
`GoodOnly` por puerta: la calidad efectiva del descriptor confirmado es la única autoridad.

Firma completa de reproducibilidad:

```text
TraceIdentity + SourceInspectionRevision + GatePlanId + GatePlanRevision
+ GateId + GateRevision + ProcessingContractVersion + AlgorithmVersion
+ EffectiveTraceDescriptor/SampleAxis + EffectiveSignalDomain/Parameters
```

En Fase 1, `GatePlanCompiler` produce además una firma SHA-256 canónica y versionada del plan declarado:
identidad y revisión del plan, versiones de esquema y algoritmo, orden declarado, definiciones completas,
dependencias, parámetros y medidas solicitadas. Los `double` se codifican por sus bits IEEE 754 y `+0.0` y
`-0.0` se normalizan a una única representación antes de calcular la firma.

`TraceIdentity` ya distingue inspección, run, fuente, canal físico, beam y ley focal opcionales, además de
`AcquisitionIndex`. El primer incremento no contiene `ElementId` y no lo infiere de canal, beam ni ley. Los
requisitos PA reales quedan como cuestión separada.

## 4. Eje temporal y ventanas

Para muestra original `i`:

```text
t(i) = FirstSampleOffsetSeconds + i / SampleRateHz
```

La primera versión usa una ventana continua semiabierta `[start, end)`, con `end = start + duration`. Una muestra se
incluye si `start <= t(i) < end`. Conversión conceptual:

```text
first = ceil((start - offset) * fs)
endExclusive = ceil((end - offset) * fs)
```

La implementación deberá definir aritmética robusta alrededor de enteros/ULP y probarla; no redondeará
primero a ticks ni hará clamp silencioso. Se exige la ventana completa; cualquier extremo fuera
de la traza produce `WindowOutOfRange`. Una ventana dentro del eje que resuelva cero muestras produce
`EmptyResolvedWindow`, con diagnóstico homónimo; no llega al detector. Evaluar una intersección
parcial degradada no forma parte del primer incremento.

Una puerta ligada empieza en `DetectionTimeSeconds(parent) + offset`. Usa la posición submuestra, si existe,
no un índice redondeado. Solo `Detected` satisface la referencia.

## 5. Dominios de señal

| Dominio | Definición aceptada | Rango inicial | Estado |
| --- | --- | --- | --- |
| `RawSigned` | muestra Int16 firmada sin transformar | -32768..32767 counts | inicial |
| `Positive` | `max(x, 0)` | 0..32767 counts | inicial |
| `NegativeMagnitude` | `max(-widen(x), 0)` | 0..32768 counts | inicial |
| `Absolute` | `abs(widen(x))`; `abs(-32768)=32768` | 0..32768 counts | inicial |
| `RectifiedInput` | señal adquirida rectificada con encoding/calibración propios | por definir | aplazada |
| `Envelope` | envolvente según algoritmo/versionado por definir | por definir | futura/no compilable |

`RectifiedInput` no es alias de `Absolute`. La señal rectificada real se aplaza hasta resolver su encoding
persistente. La envolvente no se aproxima mediante absoluto.

Un umbral normalizado `u` en `[0,1]` se convierte en `double`, sin redondeo previo ni clamp:
`RawSigned=-32768+65535u`, `Positive=32767u` y `NegativeMagnitude/Absolute=32768u`. Se rechazan valores
no finitos o fuera del intervalo.

RF, componentes y absoluto pueden evaluarse como accessors promovidos sin buffer por defecto. Materializar
una transformación común solo se adoptará si un benchmark demuestra beneficio.

## 6. Matriz de detectores y semánticas

| Detector | Evento | Empate/orden | Sin evento | Dominios candidatos |
| --- | --- | --- | --- | --- |
| `GlobalMaximum` | mayor valor transformado de ventana no vacía | primer índice temporal | `BelowThreshold` solo si existe criterio de aceptación; si no, siempre detecta | RawSigned, Positive, NegativeMagnitude, Absolute |
| `GlobalMinimum` | menor valor transformado | primer índice temporal | misma regla | RawSigned, Positive, NegativeMagnitude, Absolute |
| `AbsoluteMaximum` | mayor `abs(widen(RF))` | primer índice; devuelve magnitud y muestra RF firmada separadas | idem | RawSigned |
| `FirstRisingCrossing` | primer flanco ascendente válido | tiempo menor | `NoEvent`; `BelowThreshold` solo por aceptación adicional | dominios ordenados compatibles |
| `LastRisingCrossing` | último flanco ascendente válido | tiempo mayor | idem | idem |
| `FirstFallingCrossing` | primer flanco descendente válido | tiempo menor | idem | idem |
| `LastFallingCrossing` | último flanco descendente válido | tiempo mayor | idem | idem |
| `FirstLocalMaximum` | primer pico que supera vecindad, prominencia y distancia | posición menor tras supresión | `NoEvent`; `BelowThreshold` si solo falla aceptación | RawSigned, Positive, NegativeMagnitude, Absolute |
| `LastLocalMaximum` | último pico válido | posición mayor tras supresión | idem | idem |

### 6.1 Cruces

Sin histéresis, ascendente entre `i` e `i+1` si `y[i] < T && y[i+1] >= T`; descendente si
`y[i] > T && y[i+1] <= T`. La igualdad pertenece al lado alcanzado. Una meseta igual a T dispara una
vez al entrar y no repetidamente. La primera muestra de ventana no forma cruce sin una premuestra; leer
fuera de la ventana requeriría contrato expreso.

Con histéresis se usan dos límites `Low <= High`: ascendente queda inicialmente armada solo si la primera
muestra es `<= Low`; si empieza dentro de la banda o por encima, espera desarmada hasta observar `<= Low`, y
dispara en el primer `>= High`. Descendente queda inicialmente armada solo si la primera muestra es `>= High`;
si empieza dentro de la banda o por debajo, espera primero `>= High` y dispara en el primer `<= Low`. Tras
disparar queda desarmada hasta el rearme opuesto. First/Last se resuelven por orden temporal.

La regla usa interpolación lineal en el límite de disparo. `Tcross=T` sin histéresis, `High` para un
ascendente con histéresis y `Low` para un descendente con histéresis. Para el par `i,i+1`, la posición es
`i + (Tcross-y[i])/(y[i+1]-y[i])`; el denominador no puede ser cero en un flanco válido.
`DetectionTimeSeconds` procede de esa posición, `FractionalSamplePosition` la conserva y `SampleIndex=i` es
siempre el anclaje izquierdo. No se redondea para comparar. Un cruce sin flanco válido produce `NoEvent`;
`BelowThreshold` se reserva a candidatos que fallan un criterio de aceptación adicional y explícito.

### 6.2 Máximos locales

Parámetros tipados aceptados:

- radio de vecindad entero `>= 1` muestras;
- prominencia mínima `>= 0` counts;
- distancia mínima `>= 1` muestras;
- posición de meseta: centro inferior;
- política de borde: excluir candidatos sin vecindad completa.

Una meseta candidata es un tramo contiguo `[a,b]` de igual valor. El radio se mide hacia fuera desde sus
bordes: se exigen las muestras `[a-radius,a-1]` y `[b+1,b+radius]`, y el valor de meseta debe ser estrictamente
superior a todas ellas. Se excluye cualquier candidato sin esa vecindad completa a ambos lados.
Su posición e índice son `floor((a+b)/2)`, el centro inferior de la meseta. Prominencia es
`peak - max(leftMinimum, rightMinimum)`, buscando a cada lado hasta la primera muestra estrictamente superior;
si no existe, termina en el borde y usa el mínimo observado hasta ese borde como base. El borde puede cerrar
una cuenca, pero nunca ser candidato.

Tras filtrar prominencia, la distancia mínima se aplica mediante supresión determinista: amplitud descendente,
luego prominencia descendente, luego posición ascendente; se suprime otro candidato a distancia menor que el
mínimo. First/Last se elige finalmente por posición. Empates globales eligen el primer índice. No se redondea
amplitud, prominencia ni posición durante detección; el redondeo es solo de presentación/serialización definida.

## 7. Dependencias y compilación

El grafo tiene un nodo por `GateId` y una arista padre→hija por referencia de inicio. El compilador rechaza:

- `GateId` duplicado/default, revisiones inválidas o límite configurado excedido;
- cero puertas; la cardinalidad válida es de 1 al máximo configurado;
- autorreferencias e IDs inexistentes;
- ciclos, incluidos los indirectos;
- duración, offsets, umbrales, histéresis o parámetros no finitos/fuera de rango;
- detector/dominio/medidas incompatibles o dominios futuros.

El máximo configurado predeterminado es 8 y el máximo absoluto es 16. El orden topológico usa el orden declarado
como desempate entre nodos listos; nunca el orden incidental de `Dictionary` ni del scheduler. Las dependencias
se compilan a índices sobre la colección declarada para evitar búsquedas durante la futura evaluación.

La compilación devuelve un resultado discriminado: `Compiled` contiene necesariamente un plan compilado
completo; cualquier estado fallido contiene errores estructurados y ningún plan parcial. El compilador comprueba
la cancelación antes del punto de publicación; después de publicar, una cancelación posterior no invalida el
resultado ya entregado.

Ejemplos conceptuales:

```text
Independientes: A   B   C        orden declarado A,B,C
Cadena:         A -> B -> C      B parte de evento(A); C de evento(B)
Abanico:        A -> B           B y C usan evento(A)
                 \-> C
Fallo:          A=NoEvent -> B=DependencyNotDetected(A, NoEvent)
Ciclo inválido: A -> B -> C -> A => plan rechazado, no se evalúa
```

No existe fallback al inicio de traza. En una cadena, el fallo conserva obligatoriamente la causa inmediata;
la raíz causal completa queda fuera del primer incremento.

## 8. Resultados, estados, medidas y diagnósticos

`GateEvaluationResult` conceptual:

```text
ReproducibilitySignature:
  TraceIdentity + SourceInspectionRevision + GatePlanId + GatePlanRevision
  + GateId + GateRevision + ProcessingContractVersion + AlgorithmVersion
  + EffectiveTraceDescriptor/SampleAxis + EffectiveSignalDomain/Parameters
Status: GateResultStatus
DetectionTimeSeconds?           respecto al trigger/origen temporal de traza
FractionalSamplePosition?
SampleIndex?                    índice original, no índice de buffer transformado
Amplitude? + AmplitudeUnit + SignalDomain
SignedRfAmplitude? / Magnitude? cuando el detector lo necesite
TimeOfFlightSeconds?             absoluto respecto al origen de traza
OffsetWithinWindowSeconds?      no se sobrecarga como TOF
Quality + detector metrics
DiagnosticCode + CausalGateId?/CausalStatus?
```

| Estado | Semántica |
| --- | --- |
| `Detected` | evento y todas las medidas solicitadas válidas |
| `BelowThreshold` | candidato/extremo existe pero incumple un criterio de aceptación definido |
| `NoEvent` | detector válido no encuentra cruce/pico/evento |
| `DependencyNotDetected` | puerta padre no terminó `Detected` |
| `InvalidTrace` | la calidad efectiva confirmada es `Invalid`; no se leen muestras |
| `WindowOutOfRange` | ventana resuelta inválida respecto al eje de muestra |
| `Cancelled` | batch terminal completo; todas sus puertas están Cancelled y no conservan medidas parciales |
| `Faulted` | fallo interno inesperado; plan inválido nunca llega al evaluator |

Diagnósticos son enums/códigos y escalares acotados; mensajes sirven para logs, no como contrato. Calidad no
es un `double` sin definición: usa `Good/Degraded/Invalid` y flags versionadas. Una traza `Degraded` se procesa
y conserva calidad degradada; una traza `Invalid` no se evalúa y produce `InvalidTrace` sin recorrer muestras.
Se conservan `Interpolated`/`Discrete` y métricas como prominencia o margen.

`GateDetectorMetrics` siempre existe: `ProminenceCounts` y `ThresholdMarginCounts` son nullable para expresar
ausencia explícita. La prominencia solo aplica a máximos locales; el margen solo a criterios de aceptación.
`GateDiagnosticCode` distingue `WindowOutsideTrace`, `EmptyResolvedWindow`, `NoEvent` y
`BelowAcceptanceThreshold` sin depender de mensajes libres.

El tiempo canónico `DetectionTimeSeconds` y el TOF se refieren siempre al origen temporal de traza. Una puerta
ligada conserva por separado la identidad y estado de su causa. El tiempo desde inicio de ventana usa el nombre
distinto `OffsetWithinWindowSeconds`.

Cada medida solicitada puede declarar una capa derivada:

```text
GateLayerKey = plan/revision + gate/revision + MeasureKind + unit + domain
```

Las capas iniciales son amplitud, tiempo de detección, TOF, estado y calidad. Las tres capas de medida solo
aportan valor con medida válida; estado y calidad conservan todos los resultados. Ausencias generan
`NoData(reason, provenance)`. Posición se une más tarde por `TraceIdentity`.

## 9. Pipeline, ownership y cierre

```text
ApplicationSession confirma RF en autoridad
  -> aviso sample-free (TraceIdentity, InspectionRevision)
  -> scheduler por watermarks acotados; señales coalescidas, sin drop lógico
  -> worker obtiene lease exacto de IUtTraceProvider
  -> GateEvaluator evalúa bajo el lease
  -> sink transitorio esperado, sin retención ni durabilidad
  -> lease.Dispose en finally
```

El aviso visual `ICommittedTraceSink` no se reutiliza. `ICommittedGateProcessingIngress` expresa admisión,
saturación y cierre. Nunca desaparece una identidad confirmada silenciosamente. La señal no basta para cubrir
la carrera commit→enqueue: el cursor/watermark reconcilia el catálogo paginado de la autoridad. El catálogo
comparte el máximo de entradas configurado del store y el scheduler no conserva una cola por traza. Al sellar
una run, procesamiento compara su cobertura con
`FinalRevision` antes de declararse drenado.

Orden de cierre aceptado: cerrar admisión RF/procesamiento; detener productor y drenar commits; sellar la run
RF y obtener `FinalRevision`; reconciliar trabajo pendiente; drenar todo el backlog admitido; esperar workers
y leases; cerrar el sink transitorio; cerrar store RF. Stop no cancela trabajo admitido como política normal.
Un timeout solo diagnostica y no fuerza liberación. El único callback externo se espera antes de liberar el
lease; el batch no contiene ni presta muestras y el sink no puede retenerlo.

El lote de una traza se confirma todo-o-nada. Si se observa cancelación tras admitir el work item, se descartan
intermedios y el evaluator devuelve un batch completo con todas las puertas `Cancelled`, sin medidas. Fase 3
solo lo contabiliza; la confirmación atómica de derivados corresponde a Fase 4. Las dependencias no se evalúan
dentro de un batch cancelado. Se comprueba antes de adquirir lease,
entre puertas y por bloques largos con stride medido/documentado.

## 10. Store derivado, invalidación y reprocesado

### Reprocesado por bloques de Fase 5

`IInspectionSealedRunSnapshotProvider` abre una vista contractual de una run sellada ligada a su
`SourceFinalRevision`. Enumera identidades confirmadas mediante cursores opacos y páginas de hasta 256; el
scheduler cruza esa membresía con streams e intervalos inclusivos normalizados y forma bloques sobre las
identidades resultantes. Cada lease RF se mantiene solo durante la evaluación de su traza y se libera antes de
esperar el bloque siguiente. Los huecos numéricos no generan trabajo.

El scheduler histórico no reutiliza `CommittedGateProcessingOrchestrator`: posee admisión y workers acotados,
round-robin cooperativo entre trabajos y un pool de scratch exclusivo. El pipeline live conserva su propio pool
y workers. La composición debe sumar ambos presupuestos configurados; instancias adicionales quedan fuera de
esa suma y no permiten afirmar un límite global.

El lifecycle monotónico es `Created → Admitted → Running → Completing → Completed`, con la rama
`Cancelling → Cancelled` y fallo terminal `Faulted`. El commit admitido al store derivado se completa aun si
se solicita cancelación. El prefijo cancelado contiene solo `Committed` y `Duplicate`; evaluaciones no admitidas
se contabilizan aparte. Stop cancela el backlog histórico, espera ownership ya admitido y no cancela live.

`StoreOnly` publica evidencia terminal sin cambiar la vista activa. `ActivateOnComplete` exige un view ID,
publica el mismo manifest exacto y activa una vez, atómicamente. Cualquier fallo o cancelación conserva la vista
anterior. El diagnóstico separa nuevos/reutilizados y no contiene RF.

El store derivado inicial es en memoria y acotado. Su implementación reside en `UTStudio.Processing.Core`,
separada del núcleo algorítmico y del commit RF. Los datos
son reconstruibles desde RF autoritativa mientras esta permanezca disponible, más plan/revisión y versión
algorítmica; el store RAM vigente no promete recuperación tras proceso. Debe permitir:

- commit inmutable e idempotente por lote de traza o granularidad aprobada: mismo key/procedencia/payload es
  duplicado; mismo key con contenido distinto es conflicto, nunca overwrite;
- consulta por `TraceIdentity`, plan, puerta, medida y revisión;
- vista activa por revisión sin mutar resultados históricos;
- invalidación lógica al publicar nueva `GateRevision`/plan revision; la revisión activa cambia atómicamente
  solo con la cobertura requerida confirmada, sin mezclar celdas de revisiones;
- reprocesado por bloques/regiones desde `IUtTraceProvider`;
- procedencia completa y política explícita de retención/GC.

La persistencia del store derivado permanece como cuestión separada; formato, compresión, checksums y
durabilidad están fuera del primer incremento. Un futuro reproceso no
carga inspecciones completas y aplica backpressure independiente de live. Consultas regionales fijan un
snapshot/revisión derivada coherente y leen páginas por stream/rango de `AcquisitionIndex`; no presuponen cubo
rectangular. Cancelled/Faulted o cobertura parcial no desplazan por sí solos una revisión activa válida.

### Contrato cerrado de Fase 4

`DerivedStoreRevision` avanza exclusivamente por commit nuevo, activación, invalidación o reclamación
confirmados. El commit copia un `GateEvaluationBatch` completo y publica todos sus resultados bajo una sola
revisión. La clave evita duplicar inspección/run/stream fuera de `TraceIdentity` y añade revisión fuente,
plan/revisión/firma, esquema y algoritmo. Cada capa añade puerta/revisión, medida, unidad y dominio.

`GateDerivedCoverageManifest` describe cobertura dispersa por stream mediante watermarks opcionales, recuento
y SHA-256 canónico v1. La entrada binaria usa versión y cantidad, identidades ordenadas por campos tipados,
GUID de inspección/run, source UTF-8 con longitud, canal/beam/ley enteros e índice `UInt64`; el conjunto vacío
firma versión y cantidad cero. Incluye revisión RF final, epoch y `CapturedStoreRevision`: la revisión exacta
del store cuyos batches pueden formar la cobertura. La activación materializa las claves exactas con commit no
posterior a esa revisión; cada lease conserva esa membresía y nunca incorpora batches posteriores. Al completar el
drenaje, el orquestador registra atómicamente en el sink la revisión terminal RF y la cobertura completa de
identidades elegibles de todos los epochs. El lote exige una sola inspección, run y revisión final; una clave de
plan repetida es conflicto sin visibilidad parcial. Activar exige igualdad exacta, verifica el
conjunto real —no el intervalo entre watermarks— y publica un `ActiveDerivedViewDescriptor` indivisible.

El ledger que retiene las identidades terminales está acotado por
`MaximumEligibleTracesPerPlanEpoch` (65 536 por defecto); alcanzar la cota rechaza nueva admisión,
incrementa saturaciones y lleva la run a fallo explícito, sin crecimiento silencioso.

Los leases de lectura son inmutables, prestados y fijan revisión/vista/epoch. Las páginas regionales admiten
1..256 elementos y sus cursores solo viven dentro del lease que los creó. `NoData` es explícito u omitido según
consulta. Dispose concurrente es idempotente. Tras cerrar el lease, las operaciones
síncronas fallan con `ObjectDisposedException`; `ReadExactAsync`, por ser el contrato
discriminado y no excepcional, devuelve `Closed`.

La política `KeepUntilInvalidatedOrClose` prohíbe expulsión automática y sobrescribir un `DerivedViewId`.
Invalidar la vista activa la elimina;
la reclamación física separada usa las membresías exactas, conserva batches compartidos y falla con `Busy` solo
ante leases que capturan claves reclamables. El presupuesto lógico v2 suma 160 bytes
por batch, 176 por entrada, cuatro bytes por medida solicitada, UTF-8 acotado de IDs causales/plan, firmas SHA-256, 128 bytes más UTF-8 por manifest o
evidencia terminal, 144 por stream y 64 más UTF-8 por descriptor de vista o tombstone de `DerivedViewId`.
Cada membresía carga 32 bytes base más 96 por clave. El tombstone permanece cargado tras reclaim para impedir reutilización. Es aritmética checked; batch y bytes se reservan antes
de copiar y se revierten íntegramente. Cubre capacidad administrada, no headers CLR, fragmentación ni working set.
Cobertura terminal, activación, invalidación y reclaim preparan colecciones completas en staging y publican
raíces coherentes bajo el lock; ningún lector observa staging ni una mutación compuesta parcial.

## 11. Presupuesto preliminar, no objetivo de producción

Payload RF por traza: `N * 2` bytes. Solo lectura de entrada a 1.000 trazas/s equivale aproximadamente a:

| Muestras | 50 trazas/s | 100 trazas/s | 1.000 trazas/s |
| ---: | ---: | ---: | ---: |
| 2.048 | 0,205 MB/s | 0,410 MB/s | 4,096 MB/s |
| 65.535 | 6,554 MB/s | 13,107 MB/s | 131,070 MB/s |

Son caudales de payload, no capacidad garantizada. No se combinan máximos de canales, beams, leyes, muestras
y tasa como requisito simultáneo.

El scratch usa leases verificables y buckets potencia de dos proporcionales al número de muestras. Cada buffer
contiene valores transformados, árbol RMQ reutilizable, candidatos y Fenwick; se reserva antes del trabajo,
está acotado por concurrencia y elementos retenidos, y no existe fallback. El límite pertenece al pool
inyectado: la composición productiva comparte una instancia entre evaluadores y no afirma un límite global si
crea pools independientes. La configuración predeterminada sigue siendo 8 puertas, máximo 16 y mínimo 1.
Agotar scratch aplica espera cancelable y contabilizada o rechazo de admisión según política configurada;
nunca reserva memoria de fallback ni bloquea sin cancelación.

Máximos locales construyen RMQ máximo/mínimo, resuelven límites estrictamente mayores, ordenan con heapsort
cancelable y aplican distancia con Fenwick. Su coste es `O(N log N)` y scratch `O(N)`; no hay rescans de
prominencia `O(N²)`. Los bucles largos, incluida la ordenación, comprueban cancelación como máximo cada 256
operaciones. Resultados asignan por puerta/lote, no por muestra.

Matriz de benchmark futura:

- N = 2.048/65.535; oferta = 50/100/1.000 trazas/s;
- ventana = 16/64 muestras, 25 % y traza completa;
- 3 independientes, A→B→C y A→{B,C}; planes de 3, 8 y 16 puertas;
- extremos, first/last crossing y máximo local; RF, absoluto y mezcla;
- streams/canales/beams/leyes y concurrencias representativas;
- live y reprocesado, incluyendo saturación, cancelación y drain.

Medir por separado lease/read, transformación, detector y commit; p50/p95/p99, throughput ofrecido/admitido/
completado, CPU, bytes asignados/traza, GC, scratch actual/máximo, profundidad/espera de cola, vida del lease,
cancelaciones y tiempo de drain. Registrar hardware, runtime, commit y configuración. Los gates productivos se
deciden después; la Fase 1 no declara objetivos productivos de rendimiento.

## 12. Estrategia de pruebas

- compilador implementado en Fase 1: IDs, self/missing/ciclos, orden declarado determinista, límites y parámetros;
- ejemplos obligatorios: 3 independientes, cadena, abanico, dependencia fallida y ciclo;
- golden/exhaustivas pequeñas: 1/2 muestras, min/max Int16, constantes, alternancia, igualdad de umbral,
  mesetas, múltiples cruces, histéresis/rearme e interpolación conocida;
- temporal: offsets/fs no enteros, límites exactos y a 1 ULP, overflow y ventana fuera de rango;
- picos: bordes, mesetas pares/impares, prominencia exacta, distancia en el límite y empates;
- metamórficas: traslación/escala positiva y simetría max/min cuando sean aplicables;
- reproducibilidad: mismo plan/traza produce resultados iguales en varios threads y reproceso;
- ownership: lease/scratch devueltos exactamente una vez en éxito, cancelación y fallo;
- carga: pool nunca excede cap, saturación no pierde identidad, cierre espera workers/leases;
- arquitectura: Core/VM sin WPF, hardware, sesión, store concreto ni geometría;
- serialización/compatibilidad de revisiones cuando se elija persistencia.

## 13. Integración futura A-Scan y C-Scan

A-Scan mostrará un snapshot neutral del plan y emitirá comandos hacia un editor/caso de uso neutral. No crea,
posee ni ejecuta el evaluator, ni guarda puertas en `AScanSnapshot`/`AScanCursorState`, ni evalúa puntos
visuales reducidos. Overlay futuro por puerta: dos límites verticales, umbral horizontal, zona
sombreada, selección visible y estado/diagnóstico. Edición de teclado requiere foco anunciado, selección de
puerta, mover inicio/fin/umbral con paso explícito, cancelación/restauración y validación neutral antes de
publicar una nueva revisión coherente. Los parámetros acoplados se publican juntos antes de notificar bindings.

La edición usa Preview + Apply/Cancel y distingue `DraftPlan` y `CommittedPlan`; comandos llevan
`expected GatePlanRevision` y pueden responder `Accepted`, `ValidationFailed` o `Conflict/StaleRevision`.
Previews de arrastre pueden coalescerse visualmente, pero solo Apply de un plan confirmado dispara
invalidación/reprocesado; Cancel descarta el draft. Atajos y hit
testing de puertas no colisionan con Home, +/- y Alt+flechas del viewport ni con cursores A/B.

Los cursores A/B actuales permanecen como overlay y comandos separados: no se convierten en límites de puerta,
no cambian el plan y conservan su advertencia de medida aproximada sobre puntos visuales reducidos. La edición
de puerta usa el eje original/contratos neutrales, no los puntos reducidos como precisión de detector.

El [plan ejecutable aceptado de Fase 6](../plans/2026-10-19-ut-gate-ascan-editor.md) concreta incrementos,
fronteras y matriz de pruebas. El [ADR 0018 aceptado](../adr/0018-editable-gate-plan-authority.md) exige un
coordinador serial asíncrono Application distinto del `_gate` actual de sesión, epochs y publicación
preparados, owner terminal de `Completion`, transición de run confirmada bajo el gate store común con CAS,
ledger normativo acotado, reservas tipadas, snapshot WPF indivisible y rechazo visible por capacidad
`LiveEpochs`. Mantiene reemplazo `StoreOnly` + CAS/abandono completo. Ha sido auditado y aceptado; Fase 6
continúa no implementada y esos contratos preparados todavía no existen.

C-Scan consumirá capas derivadas por región/identidad, no invocará el evaluator desde la View. Su máscara
distingue valor detectado, ausencia válida (`BelowThreshold`/`NoEvent`) y causas no disponibles o terminales;
no los colapsa en un único bit opaco. Geometría,
celdas, tiles y multirresolución requieren ADR propio. Capas preservan máscara `NoData`, unidad, dominio,
revisiones y procedencia.

## 14. Riesgos

| Prioridad | Riesgo | Mitigación aceptada |
| --- | --- | --- |
| P1 | usar `latest-only` y perder procesamiento | puerto acotado lossless separado y contabilidad |
| P1 | segunda autoridad/copia RF | lease breve del provider; ningún RF en resultado/core |
| P1 | resultado no reproducible | firma SHA-256 canónica y versionada con identidad, revisiones, esquema, algoritmo, eje, dominio y parámetros |
| P1 | semánticas ambiguas o no deterministas | compiler estricto, reglas matemáticas y goldens |
| P1 | backlog/leases impiden Stop | cerrar admisión, drenar todo lo admitido y medir la barrera |
| P1 | plan cíclico/fallback silencioso | rechazo previo y propagación causal explícita |
| P2 | scratch/concurrencia exceden memoria | pool y concurrencia acotados; sin fallback alloc |
| P2 | confundir canal/elemento/beam/ley | identidades tipadas; no inferir `ElementId` |
| P2 | UI edita estado parcial/reentrante | snapshot/revisión y publicación coherente |
| P2 | NoEvent se vuelve valor C-Scan | valor + máscara/causa/procedencia separados |
| P3 | optimización prematura | benchmarks reproducibles antes de targets |

## 15. Decisiones aceptadas para el primer incremento

1. Ventana mediante inicio referenciado más duración.
2. Ventana completa obligatoria; no se recorta silenciosamente.
3. Dominios `RawSigned`, `Positive`, `NegativeMagnitude` y `Absolute`.
4. Señal rectificada real aplazada hasta resolver su encoding persistente.
5. Cruces lineales con muestra izquierda, armado explícito y `NoEvent` sin flanco.
6. Máximos locales con bordes excluidos y centro inferior de meseta.
7. Tiempo canónico desde el origen de traza; TOF ligado conserva además referencia al evento padre.
8. `TraceQuality.Degraded` procesable; `TraceQuality.Invalid` no procesable.
9. Firma completa de reproducibilidad con identidades, revisiones, contrato, algoritmo, eje, dominio y parámetros.
10. Stop cierra admisión y drena todo lo admitido.
11. Store derivado inicial en memoria, acotado y reconstruible mientras exista RF autoritativa.
12. Capas iniciales de amplitud, tiempo de detección, TOF, estado y calidad.
13. Ocho puertas predeterminadas y máximo contractual de dieciséis.
14. Sin `ElementId` en el primer incremento.
15. Edición A-Scan mediante Preview + Apply/Cancel.

Q19 queda resuelta. Permanecen separadas únicamente el encoding persistente de señal rectificada, el
presupuesto final después de benchmarks, la persistencia del store derivado y los requisitos PA reales.
