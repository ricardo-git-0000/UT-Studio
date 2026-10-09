# InspectionDataStore — Fase 3

## Objetivo

Migrar A-Scan y la selección B→A a lecturas exactas mediante `IUtTraceProvider`, sin migrar todavía
la proyección B, añadir persistencia ni implementar C-Scan.

## Resultado provisional

A-Scan recibe identidades confirmadas en un mailbox latest-only, adquiere la señal autoritativa bajo
lease y publica snapshots independientes emparejados con run, identidad, revisión y versión visual.
B-Scan continúa temporalmente como proyección derivada sobre `BScanTraceStore`. La selección B→A usa
`AcquisitionIndex`, nunca `Sequence`, para recuperar la traza adquirida original.

## Decisiones confirmadas

- La autoridad A-Scan es `InspectionDataStore`.
- La notificación commit→A contiene identidad y revisión, no muestras.
- Supersession y cambio de run cancelan/invalidan candidatos y el cierre espera trabajo admitido.
- Persistencia permanece aplazada.

## Archivos modificados

Contratos de inspección, coordinación de sesión, entrega A-Scan, selección B→A, composición WPF,
pruebas y documentación de ADR/arquitectura/plan.

## Pruebas y validaciones

La validación exige build Release, Core completa, WPF completa tres veces, repetición focal de
A-Scan/store/selección/cierre, smoke manual, `git diff --check`, enlaces, referencias y paquetes.
En esta iteración esas validaciones no quedaron satisfechas; el cambio no está preparado para commit.

## Riesgos

- P1: cualquier ruta que entregue memoria de adquisición a A o publique una run retirada.
- P2: leases/callbacks no balanceados o confusión entre `Sequence` y `AcquisitionIndex`.
- P3: optimizar o persistir antes de la fase autorizada.

## Cuestiones pendientes

La migración de rangos B y la retirada progresiva de `BScanTraceStore` pertenecen a Fase 4; la
persistencia incremental pertenece a Fase 5.
