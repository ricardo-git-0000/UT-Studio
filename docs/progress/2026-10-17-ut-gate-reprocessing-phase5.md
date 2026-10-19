# Fase 5 — reprocesado histórico de gates

Estado: contrato aceptado; implementación completada y validada en `feature/ut-gate-reprocessing`; integración
en `main` pendiente de auditoría y merge. La persistencia permanece aplazada y fuera de Fase 5.

## Objetivo y resultado

Se implementó reprocesado por bloques desde snapshots RF sellados autoritativos, con scheduler histórico
acotado, publicación derivada StoreOnly/ActivateOnComplete, diagnóstico por trabajo y equivalencia con live.
No se añadió composición productiva, UI, persistencia ni C-Scan.

## Decisiones confirmadas

- `InspectionDataStore` conserva la única autoridad RF; el store derivado es reconstruible.
- No se añaden hooks, seams ni API para observar fronteras internas.
- RNG, materialización/constructor del snapshot, `ReleaseLease`, entrada exacta a `Evaluate`, retorno interno de
  buffer, construcción interna de manifest y cleanup del acumulador se clasifican
  `NotRepresentableByContract` con inspección estructural e invariantes alcanzables.
- La integración concurrente representable es `StopDuringRfCopy`: al regresar de `CopyTo`, el scheduler observa
  el token y cancela antes de marcar la frontera o invocar al evaluador. Las pruebas directas del evaluador
  demuestran separadamente su contrato de cancelación y devolución de scratch.
- `GateReprocessingAdmissionResult` rechaza estados incoherentes y copia defensivamente secundarios.

## Archivos y pruebas

La trazabilidad vigente está en [la matriz canónica](../testing/gate-reprocessing-phase5-traceability.md) y el
benchmark en [el informe Dry](../performance/2026-10-17-gate-reprocessing-dry.md).

Validación final del working tree:

- build Release: 0 errores, 0 avisos;
- Fase 5 dirigida: 92/92;
- pairwise: 23/23, incluidas 21 filas con expectativas literales;
- StopDuringRfCopy: 100 repeticiones;
- Core: 791/791 en la validación correctiva; la validación original fue 790/790 en tres pasadas;
- WPF: 134/134 en tres pasadas;
- BenchmarkDotNet Dry operacional: 10/10;
- formato, `git diff --check`, UTF-8, enlaces, paquetes, secretos y artefactos: verdes.

## Riesgos y cuestiones pendientes

Los benchmarks Dry son diagnósticos, no baseline. Las fronteras `NotRepresentableByContract` deberán recibir
pruebas específicas únicamente si en el futuro adquieren una dependencia fallable u observable mediante un
contrato productivo legítimo.

## Estado Git

En el cierre original de este resumen, la implementación base estaba confirmada en
`aee0bc1e8493192430274b1f248dba17a9cf29ff` y las correcciones posteriores aún no se habían confirmado.

### Actualización posterior — 18 de octubre de 2026

La implementación está confirmada en `aee0bc1e8493192430274b1f248dba17a9cf29ff` y las correcciones posteriores
en `91091a1c328d1e9ffe8cd519debda04df1b12a9d`, ambas en `feature/ut-gate-reprocessing`. La integración en
`main` continúa pendiente; no se afirma que esos commits formen parte de `main`. El working tree estaba limpio
en el momento de esta actualización.

### Nota posterior de integración — 19 de octubre de 2026

Los párrafos anteriores conservan los estados históricos de sus fechas. Fase 5 quedó integrada en `main` en
`eb3eb2e`, con la secuencia `aee0bc1` → `91091a1` → `f29748a` → `eb3eb2e`; la antigua rama
`feature/ut-gate-reprocessing` fue eliminada. El árbol estaba limpio en la comprobación previa a esta nota.
Fase 6 todavía no está implementada.
