# Diseño aceptado del procesamiento neutral de puertas UT

Fecha: 2026-10-16. Estado: **Fase 1 implementada; ADR 0017 aceptado**.

## 1. Alcance y autoridad

Requisitos confirmados por la petición vigente:

- procesar una traza confirmada con un plan de una puerta como mínimo y hasta el máximo configurado,
  con puertas independientes o ligadas;
- neutralidad respecto a WPF, A/C-Scan, hardware, simulador, `ApplicationSession`, store concreto y posición;
- determinismo, memoria/colas acotadas, cancelación y resultados versionados;
- `InspectionDataStore` conserva la autoridad RF; resultados de puertas son derivados reconstruibles;
- Fase 1 implementa exclusivamente contratos neutrales y compilación del plan; RF, detectores, UI, C-Scan,
  persistencia y paquetes nuevos quedan fuera de su alcance.

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

`UTStudio.Processing.Core` contiene el compilador neutral y no conoce stores, sesión, UI ni posición. Las
identidades y revisiones estables viven en `UTStudio.Domain/Processing`; los contratos públicos compartidos,
en `UTStudio.Contracts/Processing`. `Contracts` depende de `Domain`, y ninguno depende de `Processing.Core`.

Propietario de los documentos de esta fase: agente principal. Revisores especializados: solo lectura.

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

IGatePlanCompiler.Compile(GatePlan) -> GatePlanCompilationResult
IGateEvaluator.Evaluate(TraceDescriptor, IReadOnlyUtSamples,
                        CompiledGatePlan, CancellationToken) -> GateEvaluationBatch
```

Colecciones se copian al construir; no se usan diccionarios `string/object`. Cada detector tiene parámetros
tipados. Segundos son `double` finitos; amplitud inicial, cuentas digitales. El porcentaje visual del A-Scan
no es unidad de procesamiento.

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
de la traza produce `WindowOutOfRange`. Una ventana dentro del eje que resuelva cero muestras también produce
`WindowOutOfRange`, con diagnóstico `EmptyResolvedWindow`; no llega al detector. Evaluar una intersección
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
RelativeTofSeconds? + TofReference
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
| `TraceNotAvailable` | no se puede procesar: not found/gap/closed o `TraceQuality.Invalid`, distinguido por diagnóstico |
| `WindowOutOfRange` | ventana resuelta inválida respecto al eje de muestra |
| `Cancelled` | batch terminal completo; todas sus puertas están Cancelled y no conservan medidas parciales |
| `Faulted` | fallo interno inesperado; plan inválido nunca llega al evaluator |

Diagnósticos son enums/códigos y escalares acotados; mensajes sirven para logs, no como contrato. Calidad no
es un `double` sin definición: usa `Good/Degraded/Invalid` y flags versionadas. Una traza `Degraded` se procesa
y añade `SourceTraceDegraded`; una traza `Invalid` no se evalúa y produce `TraceNotAvailable` con diagnóstico
`InvalidSourceTraceQuality`. Se conservan `Interpolated`/`Discrete` y métricas como prominencia o margen.

El tiempo canónico `DetectionTimeSeconds` se refiere siempre al origen temporal de traza. Para una puerta
independiente, `RelativeTofSeconds` usa también ese origen; para una ligada, expresa adicionalmente el intervalo
desde el evento padre. El tiempo desde inicio de ventana usa el nombre distinto `OffsetWithinWindowSeconds`.

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
  -> scheduler/cola acotados, sin drop/coalescing, reconciliables por revisión
  -> worker obtiene lease exacto de IUtTraceProvider
  -> GateEvaluator evalúa bajo el lease
  -> lease.Dispose en finally
  -> writer derivado confirma lote todo-o-nada
  -> notificación sample-free a consumidores
```

El aviso visual `ICommittedTraceSink` no se reutiliza. El puerto futuro de procesamiento debe expresar
admisión, saturación y cierre; la política exacta (esperar, rechazar contabilizadamente o parar) depende de
capacidades y presupuesto. Nunca desaparece una identidad confirmada silenciosamente. La cola no basta
para cubrir la carrera commit→enqueue ni una caída: un cursor/watermark, dirty coverage o outbox futuro debe
permitir reconciliar desde la autoridad. Al sellar una run, procesamiento compara su cobertura con
`FinalRevision` antes de declararse drenado.

Orden de cierre aceptado: cerrar admisión RF/procesamiento; detener productor y drenar commits; sellar la run
RF y obtener `FinalRevision`; reconciliar trabajo pendiente; drenar todo el backlog admitido; esperar workers
y leases; sellar/finalizar derivados; cerrar store RF. Stop no cancela trabajo admitido como política normal.
Un timeout solo diagnostica y no fuerza liberación. Un worker no hace `await` ni publica callbacks externos
mientras conserva el lease.

El lote de una traza se confirma todo-o-nada. Si se observa cancelación tras admitir el work item, se descartan
intermedios y el evaluator devuelve un batch completo con todas las puertas `Cancelled`, sin medidas. El writer
lo confirma atómicamente como resultado auditable; cancelar la espera del llamador no cancela ese cleanup/commit
ya admitido. Las dependencias no se evalúan dentro de un batch cancelado. Se comprueba antes de adquirir lease,
entre puertas y por bloques largos con stride medido/documentado.

## 10. Store derivado, invalidación y reprocesado

El store derivado inicial es en memoria, acotado y un puerto separado, no parte de `Processing.Core`
algorítmico ni del commit RF. Los datos
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

## 11. Presupuesto preliminar, no objetivo de producción

Payload RF por traza: `N * 2` bytes. Solo lectura de entrada a 1.000 trazas/s equivale aproximadamente a:

| Muestras | 50 trazas/s | 100 trazas/s | 1.000 trazas/s |
| ---: | ---: | ---: | ---: |
| 2.048 | 0,205 MB/s | 0,410 MB/s | 4,096 MB/s |
| 65.535 | 6,554 MB/s | 13,107 MB/s | 131,070 MB/s |

Son caudales de payload, no capacidad garantizada. No se combinan máximos de canales, beams, leyes, muestras
y tasa como requisito simultáneo.

Un scratch `int` de 65.535 muestras cuesta 262.140 bytes; cuatro dominios materializados costarían 1.048.560
bytes por evaluación concurrente, antes de candidatos/resultados. Por ello el diseño favorece accessors y
materialización lazy compartida solo cuando un benchmark la justifique. La configuración predeterminada es
8 puertas, el máximo absoluto es 16 y la cardinalidad mínima válida es 1. La concurrencia se valida contra
presupuesto.
Agotar scratch aplica espera cancelable y contabilizada o rechazo de admisión según política configurada;
nunca reserva memoria de fallback ni bloquea sin cancelación.

Coste esperado: `O(transformaciones materializadas + suma de muestras visitadas por detector + candidatos)`;
una implementación ingenua puede ser `O(G*N)`. Resultados asignan por puerta/lote, no por muestra.

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
