# Almacenamiento e informes

Trazabilidad: R09–R12; ADR 0003–0004. Las propiedades son obligatorias; contenedor, extensión, librería, compresión y tamaños concretos permanecen abiertos.

## Persistencia propuesta
Separar datos primarios, metadatos/configuración/calibración, índices espaciales/temporales y previsualizaciones derivadas. Escribir bloques incrementales independientes y versionados, con identidad de frame, secuencia, tiempo, posición cuando exista y evidencia de discontinuidades.
Diseñar confirmaciones recuperables y validación de integridad. Tras interrupción, identificar bloques válidos, cola truncada y corrupción interior; comunicar qué no pudo recuperarse. Índices y previews deberían poder reconstruirse cuando los datos primarios lo permitan. No modificar el original durante una recuperación destructiva sin autorización.
La política de flush y durabilidad frente a corte eléctrico depende del hardware y queda pendiente: archivo cerrado, datos escritos y datos durables no significan lo mismo.
Una cola acotada protege memoria, no garantiza escritura sin pérdidas ante disco insuficiente. Coordinar saturación y fallos con Application según [pipeline](data-pipeline.md).

## Lectura y escala espacial
Inspecciones de 15–29 GB se consultan por región, tiempo o bloque; jamás se cargan completas. C/B/D-Scan emplean tiles y niveles multirresolución con caché limitada. El tamaño físico (1 × 1 m habitual, hasta 3 × 10 m previsto) no determina por sí solo número de píxeles ni memoria: hace falta paso por eje.
La reproducción implementa el contrato de fuente, con cancelación y ritmo controlable; no debe exigir hardware conectado. Los importadores dependen de interfaces propias; no inventar formatos externos prioritarios.

## Informes
Reporting produce un modelo neutral; Reporting.Pdf lo renderiza. El PDF forma parte de v1, no de la entrega documental actual. No seleccionar biblioteca ni plantilla hasta conocer PDF/A, firmas, imágenes, contenido y formato corporativo. Evitar que la generación requiera controles WPF vivos o la carga completa de la inspección.
