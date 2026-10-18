# Fase 5 — reprocesado histórico de gates

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
- La integración concurrente representable es `StopDuringRfCopy`; el scheduler propaga el token del trabajo al
  evaluador y las pruebas directas del evaluador demuestran su cancelación y devolución de scratch.
- `GateReprocessingAdmissionResult` rechaza estados incoherentes y copia defensivamente secundarios.

## Archivos y pruebas

La trazabilidad vigente está en [la matriz canónica](../testing/gate-reprocessing-phase5-traceability.md) y el
benchmark en [el informe Dry](../performance/2026-10-17-gate-reprocessing-dry.md).

Validación final del working tree:

- build Release: 0 errores, 0 avisos;
- Fase 5 dirigida: 91/91;
- pairwise: 23/23, incluidas 21 filas con expectativas literales;
- StopDuringRfCopy: 100 repeticiones;
- Core: 790/790 en tres pasadas;
- WPF: 134/134 en tres pasadas;
- BenchmarkDotNet Dry operacional: 10/10;
- formato, `git diff --check`, UTF-8, enlaces, paquetes, secretos y artefactos: verdes.

## Riesgos y cuestiones pendientes

Los benchmarks Dry son diagnósticos, no baseline. Las fronteras `NotRepresentableByContract` deberán recibir
pruebas específicas únicamente si en el futuro adquieren una dependencia fallable u observable mediante un
contrato productivo legítimo.

## Estado Git

HEAD de trabajo: `e058433c1e07d63afb3ea7011be36bd81bf81e23`. Índice vacío. No se hizo stage ni commit.
Dictamen local: **GO** para `feat(processing): reprocess stored UT traces` si la auditoría final no encuentra P1/P2/P3.
