# Fase 4 — store derivado RAM de puertas UT

## Objetivo y resultado

Implementar el store derivado neutral, acotado y reconstruible de ADR 0017, integrado como sink real de la
orquestación post-commit. La RF permanece exclusivamente en `InspectionDataStore`.

## Decisiones confirmadas

Se incorporaron a ADR, diseño y plan la revisión por mutación observable, batch atómico, clave completa,
manifest disperso con SHA-256, activación linealizable, leases coherentes, paginación 1..256,
`KeepUntilInvalidatedOrClose`, reclamación explícita, presupuesto lógico v2 y cierre drenante.

## Archivos modificados

Contratos de Domain/Processing y Contracts/Processing; store RAM y orquestador en Processing.Core;
composición productiva; pruebas Core; benchmark diagnóstico; ADR 0017, diseño y plan.

## Pruebas y validaciones

- build Release completo: correcto, sin avisos ni errores;
- Processing dirigida final: 138/138; store dirigido integrado en Core: 31/31;
- Core completa final: 700/700, tres pasadas consecutivas;
- WPF completa: 134/134, tres pasadas consecutivas;
- benchmark Dry: 1 y 64 batches, diagnóstico sin baseline;
- matriz de atomicidad, vistas y reclaim: 50/50; integración real: 20/20;
- barrera visual sensible: 100/100 con watermark causal posterior al reemplazo;
- formato limitado aplicado; `git diff --check`, UTF-8 y enlaces relativos comprobados al cierre.

## Riesgos

El presupuesto mide payload lógico administrado, no heap ni working set. El benchmark Dry no prueba caudal
productivo. Persistencia, reprocesado general y geometría permanecen fuera de alcance.
El ledger terminal exacto está acotado a 65 536 identidades por epoch de forma predeterminada; la saturación
rechaza admisión y falla la run explícitamente. El watermark diagnóstico elimina la carrera conocida de
`TimerBarrierPreservesLatestOnlyAndLeavesNoPreparatoryWorkForMeasuredAccept` sin sleeps ni timeouts ampliados.

## Cuestiones pendientes

Persistencia del store derivado y presupuesto productivo definitivo continúan en las fases posteriores ya
definidas; no se adelantaron decisiones de UI, C-Scan ni PA.
