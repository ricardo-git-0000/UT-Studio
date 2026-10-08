# Almacenamiento e informes

Trazabilidad: R03–R13; ADR 0003–0004 y [ADR 0015](../adr/0015-authoritative-inspection-data-store.md).
Las propiedades son obligatorias; contenedor, extensión, base de datos, librería, compresión, checksum,
memoria mapeada y tamaños concretos permanecen abiertos.

## Persistencia propuesta
Separar datos primarios, metadatos/configuración/calibración, índices espaciales/temporales y previsualizaciones derivadas. Escribir bloques incrementales independientes y versionados, con identidad de frame, secuencia, tiempo, posición cuando exista y evidencia de discontinuidades.
Diseñar confirmaciones recuperables y validación de integridad. Tras interrupción, identificar bloques válidos, cola truncada y corrupción interior; comunicar qué no pudo recuperarse. Índices y previews deberían poder reconstruirse cuando los datos primarios lo permitan. No modificar el original durante una recuperación destructiva sin autorización.
La política de flush y durabilidad frente a corte eléctrico depende del hardware y queda pendiente: archivo cerrado, datos escritos y datos durables no significan lo mismo.
Una cola acotada protege memoria, no garantiza escritura sin pérdidas ante disco insuficiente. Coordinar saturación y fallos con Application según [pipeline](data-pipeline.md).

## Store lógico y Fase 1 en memoria

`InspectionDataStore` es la única autoridad lógica. Fase 1 vive en `UTStudio.Inspection.Core`, con
contratos en `UTStudio.Contracts/Inspection`, exclusivamente en RAM y sin crear
`UTStudio.Inspection.Storage`. Agrupa inspecciones, runs, streams, trazas y gaps dispersos en chunks
append-only. Cada traza completa aparece atómicamente en una nueva `InspectionRevision`; índices solo
apuntan a commits confirmados. `WasAccepted` distingue admisión, `IsCommitted` distingue commit y
`Durable=false`.

`SampleCapacityBudgetBytes` limita reservas y arrays propios de muestras; `MaximumEntries` acota por
separado metadata e índices sin prometer un límite exacto del heap o working set. No usa pools compartidos
si impiden demostrar el límite, ni expulsa datos autoritativos. Capacidad insuficiente causa
backpressure o `CapacityExceeded`, nunca overwrite/drop. Chunks RAM tienen tamaño provisional
configurable —no formato de archivo— y se sellan por capacidad, cambio incompatible o cierre de run.
Lecturas exactas/rangos usan lease estable con Dispose idempotente o snapshot independiente; cierre
espera leases. Diagnósticos separan bytes lógicos, reservados, materializados y máximo materializado
de muestras, además de límites/uso de entradas, writers, leases y chunks.

En la integración de Fase 2, el runtime posee un store en memoria por inspección, con un
`InspectionId` estable. Las ejecuciones sucesivas usan identidades de run distintas dentro del mismo
store y comparten el mismo presupuesto, evitando multiplicar el límite por cada reinicio. La sesión
espera el commit autoritativo antes de publicar las proyecciones visuales derivadas y el runtime cierra
el store durante el cierre global, después de drenar la sesión.

## Persistencia futura

Un futuro journal registrará preparación, commit, revisión y sellado. Checksums y versiones permitirán distinguir
tail truncado de corrupción interior. El índice es un acelerador reconstruible desde chunks confirmados,
no autoridad independiente. Recuperación no modifica el original destructivamente sin autorización.

Cuando exista backing durable, cache caliente/fría, journal, checksum, recuperación y diagnósticos de
durabilidad se diseñarán sin cambiar la autoridad lógica. Formato, chunk persistente, compresión,
checksum, mmap, flush y política ante disco lento permanecen abiertos.

## Lectura y escala espacial
Inspecciones de 15–29 GB se consultan por región, tiempo o bloque; jamás se cargan completas. C/B/D-Scan emplean tiles y niveles multirresolución con caché limitada. El tamaño físico (1 × 1 m habitual, hasta 3 × 10 m previsto) no determina por sí solo número de píxeles ni memoria: hace falta paso por eje.
La reproducción implementa el contrato de fuente, con cancelación y ritmo controlable; no debe exigir hardware conectado. Los importadores dependen de interfaces propias; no inventar formatos externos prioritarios.

## Informes
Reporting produce un modelo neutral; Reporting.Pdf lo renderiza. El PDF forma parte de v1, no de la entrega documental actual. No seleccionar biblioteca ni plantilla hasta conocer PDF/A, firmas, imágenes, contenido y formato corporativo. Evitar que la generación requiera controles WPF vivos o la carga completa de la inspección.

## Ciclo de vida de runs y gaps

El store de inspección mantiene un presupuesto global y entrega una capacidad de escritura exclusiva
por `AcquisitionRunId`. Una run transita `Open → Sealing → Sealed`; el sellado bloquea admisiones,
espera commits admitidos y publica revisión final y watermarks. Una run sellada no se reabre, pero no
impide abrir otra run en el mismo store.

La unidad de commit en memoria contiene la traza y sus `GapRecord`, o un `GapRecord` independiente si
el frame observador demuestra la pérdida pero no supera validaciones posteriores.
Cada gap identifica el stream y un rango inclusivo de `Sequence`, sin deducir índices de adquisición.
La publicación de payload, índices, gaps y revisión es atómica; A-Scan y B-Scan reciben el frame solo
después del commit. Los gaps consumen el límite global `MaximumEntries` junto con las trazas.

El cierre de admisión, la liberación de owners/chunks y la confirmación de cleanup se contabilizan por
separado. Los errores principal y secundarios se conservan sin afirmar una transición falsa a `Sealed`.
