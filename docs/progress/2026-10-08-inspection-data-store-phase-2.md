# Fase 2 de InspectionDataStore

## Objetivo

Completar la autoridad única en `ApplicationSession`, el registro transaccional de gaps, la
exclusividad y el sellado por run, y el cierre ordenado sin implementar persistencia.

## Resultado

La unidad de commit publica conjuntamente traza, gaps asociados, índices y revisión; un gap puede ser
una unidad independiente si su frame observador se rechaza después. Cada run obtiene un writer
exclusivo, se sella después de drenar operaciones admitidas y conserva watermarks finales. La
visualización solo recibe commits confirmados y cada frame prestado se libera una vez.

## Decisiones confirmadas

- `GapRecord` usa rangos inclusivos de `Sequence` y no infiere `AcquisitionIndex`.
- El gap se confirma antes de validar índice/tiempo; un frame observador rechazado no crea traza ni visual.
- `MaximumEntries` incluye trazas y gaps.
- `Stop` no cancela commits admitidos; drena, sella y después completa el cierre.
- El store global sella runs abiertas antes de cerrar y no fuerza writers ni leases.

## Archivos modificados

Contratos de inspección, store en memoria y registro, `ApplicationSession`, composición WPF,
pruebas Core/WPF, ADR 0015, arquitectura de almacenamiento y plan por fases.

## Pruebas y validaciones

- Build Release completo: correcto, sin advertencias.
- Pruebas específicas store/sesión/integridad/cierre: 72 correctas; la tanda anterior de 68 se
  repitió tres veces y los casos añadidos se validaron en la suite completa final.
- Suite WPF: 98 correctas, repetida tres veces.
- Suite Core completa sin filtros: 624 correctas.
- Tres regresiones B-Scan: 100 ejecuciones agrupadas correctas.
- Matriz dirigida de fallos de commit y sellado: 12 casos correctos.
- `git diff --check`: correcto.

## Riesgos

El interbloqueo estaba exclusivamente en la sincronización de tres pruebas de
`BScanVisualProtocolTests`: esperaban `AfterIndexCommit` antes de liberar el candidato que retenía el
lease necesario. Las pruebas ahora conservan ambas tareas, liberan el gate desde `finally`, esperan la
indexación después de disponer el candidato y usan `DisposeAsync` como barrera productiva de drenaje.
No se modificó código productivo B-Scan.

## Cuestiones pendientes

- Revisión final del diff antes de preparar un commit; esta tarea no hizo stage ni commit.
