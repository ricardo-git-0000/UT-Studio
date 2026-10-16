# Diseño propuesto del procesamiento neutral de puertas UT

Fecha: 2026-10-16. Estado: **Fase 0 documental; propuesta pendiente de aprobación**.

## 1. Alcance y autoridad

Requisitos confirmados por la petición vigente:

- procesar una traza confirmada con un plan de al menos tres puertas independientes o ligadas;
- neutralidad respecto a WPF, A/C-Scan, hardware, simulador, `ApplicationSession`, store concreto y posición;
- determinismo, memoria/colas acotadas, cancelación y resultados versionados;
- `InspectionDataStore` conserva la autoridad RF; resultados de puertas son derivados reconstruibles;
- no implementar en esta fase código, UI, C-Scan, persistencia ni paquetes.

Las elecciones marcadas **propuesta** no son aún requisitos ni decisiones aceptadas.

## 2. Fronteras y dependencias

Grafo propuesto:

```text
UTStudio.Domain <- UTStudio.Contracts <- UTStudio.Processing.Core
                         ^                       ^
                         |                       |
        Application/orquestador        compiler + evaluator
                         |
         stores derivados / Presentation / visualización
```

`UTStudio.Processing.Core` contiene algoritmos y no conoce stores, sesión, UI ni posición. Los contratos
compartidos propuestos viven en `UTStudio.Contracts/Processing`. `GateId` y revisiones solo se moverían a
Domain si se aprueba que son lenguaje transversal. No se admite `Contracts -> Processing.Core`.

Propietario de los documentos de esta fase: agente principal. Revisores especializados: solo lectura.

## 3. Modelo inmutable y versionado

API conceptual, no firma C# definitiva:

```text
GateId                    value object no-default, orden canónico
GateRevision              revisión monotónica, no reutilizable por GateId
GatePlanId                identidad estable del plan
GatePlanRevision          revisión de contenido, topología y políticas
ProcessingContractVersion versión de serialización y semántica algorítmica

GatePlan
  Id, Revision, ContractVersion
  Gates: immutable ordered collection<GateDefinition>
  RequestedLayers: immutable collection<GateLayerDefinition>

GateDefinition
  GateId, GateRevision, SignalDomain, DetectorDefinition
  WindowDefinition, RequestedMeasures, AcceptanceCriteria

WindowDefinition                         [propuesta inicial]
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

Clave reproducible propuesta:

```text
TraceIdentity + SourceInspectionRevision + GatePlanId + GatePlanRevision
+ GateId + GateRevision + ProcessingContractVersion
```

`TraceIdentity` ya distingue inspección, run, fuente, canal físico, beam y ley focal opcionales, además de
`AcquisitionIndex`. No contiene `ElementId`: no se infiere de canal, beam ni ley. Una modalidad que requiera
elemento deberá introducir identidad tipada mediante otra decisión de contrato.

## 4. Eje temporal y ventanas

Para muestra original `i`:

```text
t(i) = FirstSampleOffsetSeconds + i / SampleRateHz
```

**Propuesta:** ventana continua semiabierta `[start, end)`, con `end = start + duration`. Una muestra se
incluye si `start <= t(i) < end`. Conversión conceptual:

```text
first = ceil((start - offset) * fs)
endExclusive = ceil((end - offset) * fs)
```

La implementación deberá definir aritmética robusta alrededor de enteros/ULP y probarla; no redondeará
primero a ticks ni hará clamp silencioso. **Propuesta:** exigir la ventana completa; cualquier extremo fuera
de la traza produce `WindowOutOfRange`. Una ventana dentro del eje que resuelva cero muestras también produce
`WindowOutOfRange`, con diagnóstico `EmptyResolvedWindow`; no llega al detector. Evaluar una intersección
parcial degradada es una alternativa pendiente.

Una puerta ligada empieza en `DetectionTimeSeconds(parent) + offset`. Usa la posición submuestra, si existe,
no un índice redondeado. Solo `Detected` satisface la referencia.

## 5. Dominios de señal

| Dominio | Definición propuesta | Rango inicial | Estado |
| --- | --- | --- | --- |
| `RfOriginal` | muestra Int16 firmada sin transformar | -32768..32767 counts | soportable |
| `PositiveComponent` | `max(x, 0)` | 0..32767 counts | soportable |
| `NegativeSigned` | `min(x, 0)` | -32768..0 counts | decisión pendiente |
| `NegativeMagnitude` | `max(-widen(x), 0)` | 0..32768 counts | alternativa explícita |
| `Absolute` | `abs(widen(x))`; `abs(-32768)=32768` | 0..32768 counts | soportable |
| `RectifiedInput` | señal adquirida rectificada con encoding/calibración propios | por definir | no soportada hoy |
| `Envelope` | envolvente según algoritmo/versionado por definir | por definir | futura/no compilable |

`RectifiedInput` no es alias silencioso de `Absolute`. Si el usuario desea el nombre “rectificada” para
full-wave calculada, deberá aprobarse como alias explícito o eliminarse para evitar dos semánticas iguales.
La envolvente no se aproxima mediante absoluto.

RF, componentes y absoluto pueden evaluarse como accessors promovidos sin buffer por defecto. Materializar
una transformación común solo se adoptará si un benchmark demuestra beneficio.

## 6. Matriz de detectores y semánticas

| Detector | Evento | Empate/orden | Sin evento | Dominios candidatos |
| --- | --- | --- | --- | --- |
| `GlobalMaximum` | mayor valor transformado de ventana no vacía | primer índice temporal | `BelowThreshold` solo si existe criterio de aceptación; si no, siempre detecta | RF, positiva, negativa explícita, absoluto |
| `GlobalMinimum` | menor valor transformado | primer índice temporal | misma regla | RF, positiva, negativa explícita, absoluto |
| `AbsoluteMaximum` | mayor `abs(widen(RF))` | primer índice; devuelve magnitud y muestra RF firmada separadas | idem | RF original |
| `FirstRisingCrossing` | primer flanco ascendente válido | tiempo menor | `NoEvent`; `BelowThreshold` solo por aceptación adicional | dominios ordenados compatibles |
| `LastRisingCrossing` | último flanco ascendente válido | tiempo mayor | idem | idem |
| `FirstFallingCrossing` | primer flanco descendente válido | tiempo menor | idem | idem |
| `LastFallingCrossing` | último flanco descendente válido | tiempo mayor | idem | idem |
| `FirstLocalMaximum` | primer pico que supera vecindad, prominencia y distancia | posición menor tras supresión | `NoEvent`; `BelowThreshold` si solo falla aceptación | RF, positiva, magnitud negativa, absoluto |
| `LastLocalMaximum` | último pico válido | posición mayor tras supresión | idem | idem |

### 6.1 Cruces

Sin histéresis, ascendente entre `i` e `i+1` si `y[i] < T && y[i+1] >= T`; descendente si
`y[i] > T && y[i+1] <= T`. La igualdad pertenece al lado alcanzado. Una meseta igual a T dispara una
vez al entrar y no repetidamente. La primera muestra de ventana no forma cruce sin una premuestra; leer
fuera de la ventana requeriría contrato expreso.

Con histéresis se proponen dos límites `Low <= High`: ascendente queda inicialmente armada solo si la primera
muestra es `<= Low`; si empieza dentro de la banda o por encima, espera desarmada hasta observar `<= Low`, y
dispara en el primer `>= High`. Descendente queda inicialmente armada solo si la primera muestra es `>= High`;
si empieza dentro de la banda o por debajo, espera primero `>= High` y dispara en el primer `<= Low`. Tras
disparar queda desarmada hasta el rearme opuesto. First/Last se resuelven por orden temporal.

La propuesta usa interpolación lineal en el límite de disparo. `Tcross=T` sin histéresis, `High` para un
ascendente con histéresis y `Low` para un descendente con histéresis. Para el par `i,i+1`, la posición es
`i + (Tcross-y[i])/(y[i+1]-y[i])`; el denominador no puede ser cero en un flanco válido.
`DetectionTimeSeconds` procede de esa posición, `FractionalSamplePosition` la conserva y `SampleIndex=i` es
siempre el anclaje izquierdo. No se redondea para comparar. Un cruce sin flanco válido produce `NoEvent`;
`BelowThreshold` se reserva a candidatos que fallan un criterio de aceptación adicional y explícito.

### 6.2 Máximos locales

Parámetros tipados propuestos:

- radio de vecindad entero `>= 1` muestras;
- prominencia mínima `>= 0` counts;
- distancia mínima `>= 1` muestras;
- posición de meseta: centro fraccional;
- política de borde: excluir candidatos sin vecindad completa.

Una meseta candidata es un tramo contiguo `[a,b]` de igual valor. El radio se mide hacia fuera desde sus
bordes: se exigen las muestras `[a-radius,a-1]` y `[b+1,b+radius]`, y el valor de meseta debe ser estrictamente
superior a todas ellas. La propuesta excluye cualquier candidato sin esa vecindad completa a ambos lados.
Su posición es el centro fraccional y `SampleIndex=floor(position)`. Prominencia es
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
- menos de tres puertas; tres es cardinalidad mínima válida y capacidad mínima soportada;
- autorreferencias e IDs inexistentes;
- ciclos, incluidos los indirectos;
- duración, offsets, umbrales, histéresis o parámetros no finitos/fuera de rango;
- detector/dominio/medidas incompatibles o dominios futuros.

Orden topológico: Kahn con cola priorizada por representación canónica de `GateId`; nunca orden incidental de
`Dictionary` ni del scheduler.

Ejemplos conceptuales:

```text
Independientes: A   B   C        orden canónico A,B,C
Cadena:         A -> B -> C      B parte de evento(A); C de evento(B)
Abanico:        A -> B           B y C usan evento(A)
                 \-> C
Fallo:          A=NoEvent -> B=DependencyNotDetected(A, NoEvent)
Ciclo inválido: A -> B -> C -> A => plan rechazado, no se evalúa
```

No existe fallback al inicio de traza. En una cadena, el fallo se propaga conservando la causa inmediata y,
si se aprueba, la raíz causal en diagnóstico.

## 8. Resultados, estados, medidas y diagnósticos

`GateEvaluationResult` conceptual:

```text
Identity: TraceIdentity + plan/revisiones + gate/revisión + source revision
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
| `TraceNotAvailable` | el orquestador no obtiene la traza; diagnóstico distingue not found/gap/closed |
| `WindowOutOfRange` | ventana resuelta inválida respecto al eje de muestra |
| `Cancelled` | batch terminal completo; todas sus puertas están Cancelled y no conservan medidas parciales |
| `Faulted` | fallo interno inesperado; plan inválido nunca llega al evaluator |

Diagnósticos son enums/códigos y escalares acotados; mensajes sirven para logs, no como contrato. Calidad no
es un `double` sin definición: se propone `Good/Degraded/Invalid`, flags versionadas (`SourceTraceDegraded`,
`Interpolated`, `Discrete`, y `PartialWindow` solo si se aprueba) y métricas como prominencia o margen.

Para puerta independiente, **propuesta:** `RelativeTofSeconds` se refiere a origen temporal de traza; para
ligada, al evento padre. `DetectionTimeSeconds` siempre se conserva, y tiempo desde inicio de ventana usa
el nombre distinto `OffsetWithinWindowSeconds`.

Cada medida solicitada puede declarar una capa derivada:

```text
GateLayerKey = plan/revision + gate/revision + MeasureKind + unit + domain
```

Amplitud y TOF son capas distintas. Solo `Detected` con medida válida aporta valor; los demás estados generan
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
capacidades y queda pendiente. Nunca desaparece una identidad confirmada silenciosamente. La cola no basta
para cubrir la carrera commit→enqueue ni una caída: un cursor/watermark, dirty coverage o outbox futuro debe
permitir reconciliar desde la autoridad. Al sellar una run, procesamiento compara su cobertura con
`FinalRevision` antes de declararse drenado.

Orden de cierre propuesto: cerrar admisión RF/procesamiento; detener productor y drenar commits; sellar la run
RF y obtener `FinalRevision`; reconciliar trabajo pendiente; drenar o cancelar backlog según política aprobada;
esperar workers y leases; sellar/finalizar derivados; cerrar store RF. Un timeout solo diagnostica y no fuerza
liberación. Un worker no hace `await` ni publica callbacks externos mientras conserva el lease.

El lote de una traza se confirma todo-o-nada. Si se observa cancelación tras admitir el work item, se descartan
intermedios y el evaluator devuelve un batch completo con todas las puertas `Cancelled`, sin medidas. El writer
lo confirma atómicamente como resultado auditable; cancelar la espera del llamador no cancela ese cleanup/commit
ya admitido. Las dependencias no se evalúan dentro de un batch cancelado. Se comprueba antes de adquirir lease,
entre puertas y por bloques largos con stride medido/documentado.

## 10. Store derivado, invalidación y reprocesado

El store derivado es un puerto separado, no parte de `Processing.Core` algorítmico ni del commit RF. Los datos
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

Persistencia, formato, compresión, checksums y durabilidad permanecen fuera de Fase 0. Un futuro reproceso no
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
materialización lazy compartida solo cuando un benchmark la justifique. El máximo de puertas y concurrencia
serán configurables y validados contra presupuesto; no se fija un número arbitrario. El mínimo funcional es 3.
Agotar scratch aplica espera cancelable y contabilizada o rechazo de admisión según política configurada;
nunca reserva memoria de fallback ni bloquea sin cancelación.

Coste esperado: `O(transformaciones materializadas + suma de muestras visitadas por detector + candidatos)`;
una implementación ingenua puede ser `O(G*N)`. Resultados asignan por puerta/lote, no por muestra.

Matriz de benchmark futura:

- N = 2.048/65.535; oferta = 50/100/1.000 trazas/s;
- ventana = 16/64 muestras, 25 % y traza completa;
- 3 independientes, A→B→C y A→{B,C}; 3 puertas y límites configurados candidatos;
- extremos, first/last crossing y máximo local; RF, absoluto y mezcla;
- streams/canales/beams/leyes y concurrencias representativas;
- live y reprocesado, incluyendo saturación, cancelación y drain.

Medir por separado lease/read, transformación, detector y commit; p50/p95/p99, throughput ofrecido/admitido/
completado, CPU, bytes asignados/traza, GC, scratch actual/máximo, profundidad/espera de cola, vida del lease,
cancelaciones y tiempo de drain. Registrar hardware, runtime, commit y configuración. Los gates productivos se
deciden después; esta Fase 0 no declara objetivos.

## 12. Estrategia de pruebas futura

- compilador: IDs, self/missing/ciclos, orden estable bajo permutaciones, límites y parámetros;
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

La edición propuesta distingue `DraftPlan` y `CommittedPlan`; comandos llevan `expected GatePlanRevision` y
pueden responder `Accepted`, `ValidationFailed` o `Conflict/StaleRevision`. Previews de arrastre pueden
coalescerse visualmente, pero solo Apply de un plan confirmado dispara invalidación/reprocesado. Atajos y hit
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

| Prioridad | Riesgo | Mitigación propuesta |
| --- | --- | --- |
| P1 | usar `latest-only` y perder procesamiento | puerto acotado lossless separado y contabilidad |
| P1 | segunda autoridad/copia RF | lease breve del provider; ningún RF en resultado/core |
| P1 | resultado no reproducible | clave con plan/gate/schema/source revision |
| P1 | semánticas ambiguas o no deterministas | compiler estricto, reglas matemáticas y goldens |
| P1 | backlog/leases impiden Stop | admisión, drain/cancel y métricas propios |
| P1 | plan cíclico/fallback silencioso | rechazo previo y propagación causal explícita |
| P2 | scratch/concurrencia exceden memoria | pool y concurrencia acotados; sin fallback alloc |
| P2 | confundir canal/elemento/beam/ley | identidades tipadas; no inferir `ElementId` |
| P2 | UI edita estado parcial/reentrante | snapshot/revisión y publicación coherente |
| P2 | NoEvent se vuelve valor C-Scan | valor + máscara/causa/procedencia separados |
| P3 | optimización prematura | benchmarks reproducibles antes de targets |

## 15. Decisiones pendientes del usuario

1. Ubicación definitiva de identidades (`Domain` o `Contracts/Processing`).
2. `inicio + duración` (recomendado) o inicio/fin referenciados independientemente.
3. Ventana completa obligatoria (recomendada) o recorte degradado explícito.
4. Exponer `NegativeSigned`, `NegativeMagnitude` o ambos; significado de “rectificada”.
5. Aceptar o cambiar la propuesta de cruces interpolados, anclaje izquierdo, armado y `NoEvent`.
6. Aceptar o cambiar la propuesta de bordes excluidos, prominencia, distancia y centro de meseta.
7. TOF independiente respecto a origen de traza (recomendado) o inicio de ventana.
8. Tratamiento de `TraceQuality=Degraded/Invalid` y modelo final de calidad.
9. Revisión mínima reproducible: plan/gate/schema/algoritmo y reglas de monotonicidad.
10. Política de saturación y garantía de Stop: drenar todo lo admitido o cancelar con ledger explícito.
11. Cursor/outbox/reconciliación, atomicidad/retención/GC del store derivado y cuándo requiere persistencia.
12. Capas/medidas C-Scan publicables y calidades aceptadas.
13. Máximo configurable de puertas, concurrencia y presupuesto tras benchmarks.
14. Necesidad real de `ElementId` en la primera modalidad de procesamiento.
15. Edición A-Scan con preview+Apply/Cancel, solapamiento/hit testing y unidades de umbral por dominio.

No se implementará ninguna de estas propuestas hasta aprobar el ADR y el plan.
