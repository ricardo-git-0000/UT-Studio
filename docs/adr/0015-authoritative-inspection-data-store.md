# ADR 0015 — Almacén autoritativo de datos de inspección

Fecha: 2026-10-05. Estado: Aceptado.
Trazabilidad: R03–R10 y R13; ADR 0002, 0003, 0007, 0008, 0013 y 0014.

## Contexto

La canalización actual entrega cada `ConventionalUtFrame` desde `ApplicationSession` a ramas visuales
síncronas. A-Scan conserva snapshots latest-only y B-Scan copia RF a `BScanTraceStore`. Esa solución es
acotada para visualización en vivo, pero no constituye la fuente permanente de una inspección. Cerrar
una ventana no debe eliminar datos, y una inspección de 15–29 GB no puede residir completa en RAM.

Se necesita una autoridad neutral entre adquisición y todos los consumidores:

```text
simulador / GigE / PCIe / archivo
              ↓
      ApplicationSession
              ↓
     InspectionDataStore
              ↓
 proveedores A/B y futuros C/S/D ──→ persistencia incremental
```

## Decisión

`InspectionDataStore` será la única fuente autoritativa de trazas aceptadas. Adquisición no escribirá
en stores visuales paralelos. Los controles y ViewModels solo conservarán estado de presentación,
snapshots o leases acotados; nunca serán propietarios del conjunto adquirido. Latest-only se permite
después del store, exclusivamente en visualización.

El contrato será neutral y no contendrá WPF/Avalonia ni detalles de archivo, base de datos, compresión
o memoria mapeada. Los contratos se prevén en `UTStudio.Contracts/Inspection`; Application coordinará
sesión y cierre; el store neutral en memoria se prevé en el nuevo proyecto
`UTStudio.Inspection.Core`, dependiente de Domain y Contracts; Visualization.Core consumirá sus
proveedores neutrales. No se crea todavía `UTStudio.Inspection.Storage`.

## Modelo lógico

- Una `InspectionId` agrupa una inspección y puede contener varias adquisiciones o reaperturas.
- Cada inicio/reanudación crea una `AcquisitionRunId`; no se finge continuidad entre runs.
- Una run declara fuente, revisiones de configuración y streams. La adquisición convencional de estas
  fases conserva `UtSourceId`, canal físico, beam y ley focal por separado. No expone todavía dimensión
  de elemento; cuando una modalidad la incorpore deberá usar identidad tipada propia, sin colapsarla ni
  deducirla de canal, beam o ley.
- En Fase 1, `TraceIdentity` contiene `InspectionId`, `AcquisitionRunId`, `UtSourceId`,
  `PhysicalChannelId`, `BeamId?` o `FocalLawId?` tipados y `AcquisitionIndex`. Beam y ley no se
  colapsan ni se infieren; las combinaciones válidas se comprueban contra la configuración.
  `Sequence` permanece en el descriptor para integridad y gaps, pero no sustituye a
  `AcquisitionIndex` ni forma parte de esta identidad confirmada.
- `TraceDescriptor` reúne identidad, `Sequence`, tiempo monotónico desde inicio, UTC opcional y
  calidad de reloj, posición opcional, eje de muestra (`SampleCount`, frecuencia y offset temporal),
  representación RF/rectificada, encoding, procedencia y calidad.
- La posición es un snapshot disperso de coordenadas tipadas con eje, unidad, referencia/revisión y
  calidad. No exige X/Y/Z ni un encoder concreto.
- `GapRecord` representa en esta fase intervalos ausentes o incompletos con stream, `Sequence`, causa y
  evidencia observada. Rangos de índice/tiempo/posición requerirán una extensión contractual futura;
  no se inventan a partir de `Sequence`. Un gap no es una traza de ceros.
- Las muestras adquiridas son evidencia primaria. Índices, previews, raster, reducciones, gates y
  reconstrucciones son derivados con procedencia hacia identidades y revisión.

La inspección es un conjunto disperso append-only de runs, streams, trazas y gaps. No se presupone una
traza para cada combinación ni un cubo rectangular completo. PA puede requerir en el futuro identidad
de trigger/grupo para detectar imágenes incompletas; no se inventa en esta fase.

## Contratos conceptuales

Las firmas exactas pertenecen a Fase 1; la semántica aceptada es:

- `IInspectionDataWriter`: una sola autoridad de escritura por run; acepta append de descriptor y
  memoria prestada, publica resultado explícito y permite drenar/sellar. En Fase 1, `WasAccepted`
  indica que la operación superó la validación inicial y fue admitida como writer antes del cierre;
  no es un resultado terminal. `IsCommitted` indica que identidad, descriptor, muestras, chunk,
  índices y revisión ya son visibles atómicamente. `Durable` es siempre `false` porque no existe
  backing durable. El estado terminal distingue `Committed`, `Duplicate`, `Conflict`,
  `CapacityExceeded`, `Closed`, `Cancelled` y `Faulted`.
- `IInspectionDataReader`: abre un snapshot lógico acotado de una `InspectionRevision` y consulta
  metadatos, runs, chunks y gaps sin cargar la inspección completa.
- `IUtTraceProvider`: obtiene exactamente una `TraceIdentity` y devuelve
  `Available`, `NotAvailable`, `Gap`, `Cancelled` o `Faulted`.
- `IInspectionRangeProvider`: consulta un stream y rango de `AcquisitionIndex`; devuelve resultados
  acotados, orden, gaps y revisión coherente. Extensiones por Sequence, tiempo o posición quedan para
  fases posteriores sin alterar la identidad exacta.
- `TraceLease`: lectura inmutable con lifetime explícito que fija solo los bloques necesarios y libera
  de forma idempotente; no expone mmap ni archivo. `TraceSnapshot` es una copia independiente opcional.
- `ChunkIdentity`: identidad opaca de bloque; nunca sustituye a `TraceIdentity`.
- `InspectionRevision`: watermark monótono de commits confirmados, distinto de revisiones visuales.
- `StoreDiagnostics`: contadores y estados acumulativos de admisión, commit, durabilidad, colas,
  leases, caché, I/O, gaps, integridad, recuperación y cierre.

`ApplicationSession` presta la memoria fuente solo hasta completar `AppendAsync`. Antes de completar,
el store debe reservar presupuesto y copiar los bytes a memoria propia o adquirir ownership mediante
un contrato explícito; nunca retiene memoria prestada después. Solo un writer puede escribir una run,
mientras se permiten lectores concurrentes sobre commits confirmados.

El append reserva capacidad antes de copiar y confirma la traza todo-o-nada. Una identidad ya
confirmada nunca se sobrescribe: repetición equivalente devuelve `Duplicate`; descriptor o payload
distinto devuelve `Conflict`; presupuesto insuficiente devuelve `CapacityExceeded` o aplica
backpressure cancelable. Cancelar antes del commit revierte reserva/copia y no publica; cancelar una
espera después del commit no deshace datos y el resultado debe permitir observar que quedó confirmado.

## Chunks RAM, commits e índice

En Fase 1, los chunks son estructuras append-only en memoria. Su tamaño provisional es configurable y
no forma parte del futuro formato de archivo. Se sellan por capacidad, cambio incompatible o cierre de
run. Cada commit publica atómicamente una única traza completa —descriptor, payload y metadatos— y
avanza `InspectionRevision`; ninguna traza queda parcialmente visible. Los índices apuntan únicamente
a commits confirmados. El tamaño de chunk persistente queda abierto.

El índice en memoria es derivado y reconstruible a partir de commits confirmados. Puede acelerarse
incrementalmente, pero nunca ser la única evidencia. Debe permitir identidad exacta y rangos sin
escanear toda la inspección residente.

Journal, checksum, versiones de archivo, reparación y recuperación pertenecen a persistencia futura.
Cuando se diseñen, distinguirán tail truncado de corrupción interior y no alterarán el original
destructivamente sin autorización. Ninguna de esas decisiones forma parte de Fase 1.

## Memoria de Fase 1 y almacenamiento futuro

Fase 1 usa límites independientes explícitos. `SampleCapacityBudgetBytes` limita exactamente la suma
de reservas y arrays propios materializados para muestras. `MaximumEntries` limita por cantidad la
metadata, chunks e índices administrados; no pretende medir su tamaño físico ni limitar exactamente el
heap o working set del CLR. No usa `ArrayPool` o `MemoryPool` compartidos para las muestras. No expulsa
datos autoritativos mientras no exista almacenamiento durable. Si no puede reservar toda una traza,
espera mediante backpressure o devuelve `CapacityExceeded`; nunca sobrescribe ni descarta.

La admisión exige aritmética comprobada:
`OwnedSampleCapacityBytes + ReservedSampleCapacityBytes ≤ SampleCapacityBudgetBytes`. Los
diagnósticos separan bytes lógicos, reservados, materializados y máximo materializado, además de
`MaximumEntries`, entradas confirmadas, writers, leases y resultados acumulados.
Un lease ofrece memoria estable e inmutable, su `Dispose` es idempotente y el cierre espera leases sin
forzarlos. Un snapshot opcional posee una copia independiente y también cuenta contra presupuesto.

No se decide todavía contenedor/formato, base de datos, compresión, memoria mapeada ni layout físico.

## Backpressure y ausencia de pérdida silenciosa

En Fase 1, el ingreso se acota por el presupuesto real en bytes y no usa políticas drop. Al agotarse,
el resultado es backpressure cancelable o `CapacityExceeded` explícito. La integración posterior
decidirá si una fuente puede esperar, pausar o debe parar; bloquear hardware no es válido sin evidencia.

Los gaps originados antes del store se conservan con causa; una pérdida interna del store es fallo,
no gap normal. La política definitiva ante disco lento permanece abierta y se validará por
configuración completa con margen medido; no se combinan máximos independientes como garantía.

## Cierre, sellado y recuperación

El cierre global: bloquea nuevos starts, solicita Stop a la fuente, mantiene el único lector drenando,
cierra admisión cuando la fuente termina, resuelve cada append aceptado, confirma o deja detectable el
último chunk, sella la run como completa/incompleta/fallida, espera commits y leases, cierra store y
libera recursos. El timeout de UI solo diagnostica; no autoriza liberar owners, truncar trabajo ni
declarar éxito. Cerrar una ventana libera su consumidor y caché derivada, no la inspección.

En Fase 1 no hay recuperación tras proceso ni watermark durable: `Durable=false`. La recuperación,
flush y supervivencia a fallos comienzan con persistencia incremental en una fase posterior.

## Diagnósticos

En Fase 1: admisiones, rechazos por cierre antes de admisión, cancelaciones y commits; `Durable=false`; bytes lógicos,
reservados y materializados de muestras, máximo materializado y presupuesto; entradas máximas y
confirmadas; writers activos, máximo y pendientes durante cierre; chunks abiertos/sellados y leases
activos/máximos. El muestreo de publicación no pierde eventos del recuento. Latencias, edad/consumidor
de leases y primer/último error podrán ampliarse sin confundirlos con los límites exactos actuales.

I/O, flush, caché fría, journal, checksum y recuperación añadirán diagnósticos solo cuando exista
persistencia.

## Presupuestos de payload

Payload RF puro, sin metadata, índice, journal, alineación, owners, copias ni margen. MB y GB son
decimales (`10^6` y `10^9` bytes); MiB es binario (`2^20` bytes):

| Muestras de 16 bits | Bytes/traza | 50 trazas/s | 100 trazas/s | 1.000 trazas/s |
| ---: | ---: | ---: | ---: | ---: |
| 2.048 | 4.096 | 0,2048 MB/s | 0,4096 MB/s | 4,096 MB/s |
| 65.535 | 131.070 | 6,5535 MB/s | 13,107 MB/s | 131,07 MB/s |

Para `C` canales activos, el payload agregado es `C × muestras × 2 × trazas/s`; solo una configuración
completa medida puede admitirse. 65.535 × 1.000 ya son 1.048,56 Mbit/s antes de overhead.

| Ventana B por stream | 2.048 muestras | 65.535 muestras |
| ---: | ---: | ---: |
| 256 trazas | 1 MiB | ≈32 MiB |
| 1.024 trazas | 4 MiB | ≈128 MiB |

La ventana agregada cumple `memoriaB = C × trazasVentana × muestras × 2`, antes de overhead, leases y
derivados. Por ejemplo, ≈128 MiB es por stream y no constituye una configuración garantizada.

Duración equivalente de 15/29 GB de payload, un stream y sin overhead:

| Escenario | 15 GB | 29 GB |
| --- | ---: | ---: |
| 2.048 × 50/s | 20,35 h | 39,33 h |
| 2.048 × 100/s | 10,17 h | 19,67 h |
| 2.048 × 1.000/s | 61,0 min | 118,0 min |
| 65.535 × 50/s | 38,15 min | 73,75 min |
| 65.535 × 100/s | 19,07 min | 36,88 min |
| 65.535 × 1.000/s | 114,44 s | 221,25 s |

Con `C` streams simultáneos, tasa × C y duración / C para un tamaño total fijo. Son escenarios aislados,
no configuraciones prometidas. Los budgets implementables incluirán todas las capas y margen medido.

## Migración A-Scan y B-Scan

1. Introducir contratos en `UTStudio.Contracts/Inspection` y store RAM en
   `UTStudio.Inspection.Core`; la autoridad cambia una sola vez en la integración.
2. `ApplicationSession` escribe al store antes de cualquier entrega visual.
3. A-Scan Live observa `IUtTraceProvider` mediante una entrega latest-only; Selected obtiene snapshot
   estable por identidad.
4. B-Scan consulta `IInspectionRangeProvider`; la selección B→A resuelve `TraceIdentity` en el store.
5. `BScanTraceStore` se mantiene temporalmente solo como cache/proyección visual derivada alimentada
   desde el provider. No recibe dual-write autoritativo; después se reduce o retira.

Durante la transición se declara en cada fase una única autoridad. Cerrar/reabrir B no altera datos.
El rango residente del provider sigue siendo autoritativo para identidad/selección; dirty solo delimita
trabajo de raster conforme a ADR 0013. Remuestreo de ADR 0014 nunca modifica datos ni gaps.

## Preparación para C-Scan

Las identidades separadas, posiciones opcionales, datos dispersos, gaps, queries regionales y lineage
permiten que un futuro C-Scan derive tiles/gates desde una revisión. Esta fase no define encoder,
layout phased-array, gates, celdas, tiles, multirresolución ni cubo; serán otro incremento y ADR.

## Consecuencias y alternativas

La autoridad única elimina divergencia entre ventanas, permite lectura histórica y hace visible el
coste de persistencia, a cambio de contratos, indexación, recuperación y coordinación de cierre más
complejos. Se descartan cargar toda la inspección, usar ViewModels/controles como repositorio, mantener
dos stores autoritativos y tratar la cola acotada como garantía de persistencia.

## Decisiones abiertas

Permanecen abiertas: tamaño de chunk persistente; formato/contenedor o base de datos; compresión; memoria
mapeada; checksum concreto; política definitiva y capacidad de absorción ante disco lento; encoding
rectificado; encoder/posición; layout y agrupación phased-array; política crudo/
procesado; durabilidad/flush; taxonomía final de calidad; gates y criterios C-Scan.

## Refinamiento aceptado de Fase 2

Cada `AcquisitionRunId` se abre una sola vez y obtiene una capacidad `IInspectionRunWriter` ligada a
esa run. La capacidad confirma una unidad indivisible formada por una traza y los `GapRecord`
confirmados con ella. Un `GapRecord` también puede constituir un commit autoritativo propio cuando el
frame que demuestra la discontinuidad todavía debe superar otras validaciones. La revisión se publica
únicamente después de insertar toda la unidad;
un fallo revierte la unidad completa. Un duplicado equivalente es idempotente y un mismo gap con
evidencia distinta es conflicto, nunca sobrescritura.

En adquisición convencional ordenada, una traza puede confirmar como máximo un intervalo contiguo de
`Sequence` inmediatamente anterior. Por ello la unidad de Fase 2 admite cero o un `GapRecord`; más de
uno se rechaza en el límite contractual. Flujos futuros capaces de demostrar varios intervalos por
unidad requerirán un contrato explícito distinto, no una relajación implícita.

`GapRecord` usa un intervalo inclusivo de `Sequence`, conserva la secuencia admitida anterior y la
posterior observada, y no inventa `AcquisitionIndex`. La discontinuidad se confirma antes de avanzar el
watermark observado y antes de validar índice o tiempo. Por ello un frame observador rechazado por otra
dimensión conserva la evidencia legítima del gap sin publicar traza ni proyección visual.

El sellado pasa `Open → Sealing → Sealed`, bloquea nuevas admisiones, espera operaciones ya admitidas y
publica revisión final y watermarks disponibles. Es idempotente y sus llamadas concurrentes comparten
resultado. `Stop` detiene la fuente, drena el lector, confirma la barrera de liberación de frames y
después sella la run, que a su vez espera cualquier commit admitido. `CloseAsync` sella las runs
abiertas y no fuerza writers ni leases.
Cerrar admisión, liberar recursos y confirmar cleanup son hechos distintos: un fallo posterior no
reabre admisión ni permite declarar falsamente una run como `Sealed`.

## Refinamiento aceptado de Fase 3

A-Scan recibe después de `Committed` únicamente `TraceIdentity` e `InspectionRevision` mediante una
notificación latest-only acotada. `AScanVisualDelivery` resuelve esa identidad con `IUtTraceProvider`,
valida identidad, revisión y run, forma el snapshot mientras mantiene el lease y lo libera siempre antes
de publicar. Una sustitución o cambio de run cancela el candidato y la comprobación de generación impide
publicaciones tardías. Los resultados `NotAvailable`, `Gap`, `OutOfRange`, `Cancelled` y `Faulted`
permanecen distinguibles en el diagnóstico visual; un fallo visual no revierte el commit ni detiene la
autoridad.

B-Scan permanece temporalmente como proyección derivada alimentada con el frame ya confirmado y
`BScanTraceStore` no se elimina. La selección B→A, en cambio, resuelve la `TraceIdentity` autoritativa
por `AcquisitionIndex` mediante `IUtTraceProvider`; `Sequence` no sustituye al índice. La persistencia
incremental continúa aplazada a Fase 5.

## Refinamiento aceptado de Fase 4: estado neutral y rangos B-Scan

`IInspectionStreamStateProvider` expone un snapshot neutral e inmutable del estado de un
`UtStreamIdentity`; `IInspectionRangeProvider` conserva sin cambios su contrato anterior. El store
completo implementa ambos puertos, y los consumidores que necesiten reconciliación reciben ambos de
forma explícita. Añadir el puerto a `IInspectionDataStore` amplía deliberadamente ese contrato compuesto,
pero no fuerza a los proveedores de rangos independientes a implementar estado.

Compatibilidad: `IInspectionRangeProvider` mantiene compatibilidad fuente y binaria porque no añade
miembros. La ampliación de `IInspectionDataStore` sí es una ruptura fuente para implementadores externos
y puede ser binariamente incompatible con implementaciones precompiladas. Deben recompilarse y añadir
`IInspectionStreamStateProvider`, o exponerse mediante un adaptador compuesto que mantenga separados los
dos puertos. Los consumidores que solo leen rangos no necesitan cambios.

El snapshot se captura bajo una única `InspectionRevision` y contiene inspección,
run, stream, estado `Open`/`Sealing`/`Sealed`/`Faulted`, revisión vigente, revisión final solo tras un
sellado confirmado, watermarks mínimo y máximo de `AcquisitionIndex` confirmado, conteos de trazas y
gaps de `Sequence`, e indicador de watermarks finales. No contiene muestras, leases ni referencias
mutables y no funciona como cola de eventos.

El snapshot valida IDs coherentes, estado conocido, conteos no negativos, ambos watermarks presentes o
ausentes y orden `Minimum <= Maximum`. Cero trazas exige ambos watermarks ausentes. Solo `Sealed` admite
`FinalRevision` y watermarks finales; `Open`, `Sealing` y `Faulted` no. Una run `Faulted` expone un
`InspectionRunFaultCode` tipado y acotado. El tipo solo ofrece propiedades `get`, no inicializadores ni
`with`, por lo que no puede fabricarse una variante contradictoria después de construirlo.

Los watermarks son límites dispersos derivados exclusivamente de trazas confirmadas. No aseguran
densidad, no avanzan por rechazos, no usan `Sequence` y solo son finales tras sellar correctamente la
run. `ResidentRange` usa estos límites autoritativos en la revisión capturada; `RepresentedRange`
describe exclusivamente la proyección visual confirmada y la región dirty solo delimita trabajo de
raster.

Un `GapRecord` de `Sequence` permanece como evidencia y diagnóstico de adquisición, pero no se
convierte en posición de `AcquisitionIndex`, columna B, gap espacial ni `TraceIdentity`. Una posición
solicitada solo es `Gap` cuando exista evidencia autoritativa explícita por `AcquisitionIndex`.
Mientras esa evidencia no exista, una ausencia dentro de los watermarks es `NotAvailable`, tanto en
run abierta como sellada; fuera de watermarks finales es `OutOfRange`. `NotAvailable` y `Gap` conservan
motivos distintos, se rasterizan como NoData, no se interpolan, no comprimen el eje y no permiten
selección B→A.

Un stream conocido solo por gaps de `Sequence` tiene cero trazas y watermarks nulos. Tras sellarlo, su
rango final es vacío y cualquier `AcquisitionIndex` solicitado es `OutOfRange`; los gaps de `Sequence`
siguen sin crear posiciones ni identidades.

### Resultado de lectura de estado

| Status | Snapshot | ErrorCode | Revisión | Significado |
| --- | --- | --- | --- | --- |
| `Unknown` | no | no | inicial | Valor `default`; nunca éxito |
| `Available` | sí | no | la del snapshot | Consulta correcta; `RunState` distingue Open, Sealing, Sealed o Faulted |
| `NotAvailable` | no | InspectionNotFound, RunNotFound o StreamNotFound | capturada | Identidad no conocida por esta autoridad |
| `Closed` | no | StoreClosed | capturada | Store cerrado a nuevas lecturas |
| `Cancelled` | no | no | inicial | Cancelación anterior al punto de captura |
| `Faulted` | no | InternalFailure | inicial | Falló la propia consulta |

`Status=Faulted` no equivale a `Snapshot.RunState=Faulted`: el primero indica que no pudo realizarse
la consulta; el segundo es una consulta `Available` de una run cuyo lifecycle falló.

`ReadStreamStateAsync` comprueba cancelación antes/durante la admisión y de nuevo dentro de la puerta
del store. Esa segunda comprobación, inmediatamente antes de leer estado, es el punto de linealización.
Una cancelación anterior devuelve `Cancelled` sin snapshot; una cancelación posterior no invalida un
snapshot ya capturado.

Los fallos internos de consulta incrementan un diagnóstico interno acotado y conservan únicamente el
nombre del tipo de fallo; el contrato público devuelve `InternalFailure` sin excepción, mensaje ni datos
sensibles. Las cancelaciones anteriores a captura no incrementan lecturas completadas.

## Fases

La ejecución se divide en Fase 0 documental y Fases 1–7 del
[plan](../plans/2026-10-05-inspection-data-store.md). Este ADR no autoriza código, proyectos, paquetes
ni selección tecnológica.
