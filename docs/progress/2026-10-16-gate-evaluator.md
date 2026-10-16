# Fase 2 — evaluador síncrono de puertas UT

Fecha: 2026-10-16.

## Objetivo

Evaluar sincrónicamente una única traza confirmada mediante un `CompiledGatePlan`, sin pipeline, store, UI,
persistencia ni nuevas dependencias.

## Resultado

- Descriptor confirmado con identidad, revisión de inspección y calidad efectiva autoritativa.
- Evaluador reentrante para los nueve detectores y cuatro dominios aprobados.
- Ventanas completas, dependencias por índice, orden topológico, causalidad y TOF absoluto.
- Batch inmutable completo; cancelación terminal completa y fallos sin batch parcial.
- Scratch proporcional por buckets, con lease verificable, presupuesto compartido por pool y devolución en `finally`.
- Prominencia de máximos locales acotada a O(N log N), sin fallback ni retención de muestras.
- Benchmarks diagnósticos para 2.048 y 65.535 muestras sin baseline productiva.

## Decisiones confirmadas

El esquema y algoritmo canónicos son versión 3. `GoodOnly` se elimina. La conversión de umbral es
`RawSigned=-32768+65535u`, `Positive=32767u` y `NegativeMagnitude/Absolute=32768u`, en `double` sin clamp.

## Archivos modificados

- contratos de procesamiento;
- `UTStudio.Processing.Core`;
- pruebas Processing;
- benchmarks de procesamiento;
- ADR 0017, diseño y plan.

## Pruebas y validaciones

- `dotnet build UTStudio.sln -c Release --no-restore`: correcto, 0 avisos y 0 errores.
- pruebas Processing dirigidas: 54/54 correctas.
- suite Core completa: tres ejecuciones consecutivas de 614/614 correctas con un worker. En ejecución paralela
  se reprodujo la carrera preexistente de `VisualDiagnosticBarrierTests` sobre el contador `Replaced`; no afecta
  a Processing y queda fuera del alcance de esta corrección.
- suite WPF completa: tres ejecuciones consecutivas de 133/133 correctas. La regresión de render se ejecutó
  además 20 veces aislada; el test ahora espera la admisión del snapshot por el ciclo de render antes del hit-test.
- regresiones sensibles de `GateEvaluatorTests`, incluida la matriz contractual, casos adversos y pool: repetidas.
- BenchmarkDotNet `Dry`: doce casos ejecutados correctamente para 2.048 y 65.535 muestras, incluidas topología
  fan-out y señal adversa; scratch completamente balanceado. Los tiempos diagnósticos fueron 5,697–11,193 ms y
  5,079–55,596 ms respectivamente, con 2,23–2,28 KB asignados por operación. Son una comprobación de ejecución
  en VM, no un baseline ni un presupuesto aprobado.
- `dotnet format --verify-no-changes` en Contracts, Processing.Core, Core.Tests y Benchmarks: correcto.
- enlaces Markdown relativos, UTF-8 estricto y `git diff --check`: correctos; sin paquetes nuevos.
- revisiones lectoras de arquitectura, dominio, procesamiento, adquisición, calidad y repositorio completadas.

## Riesgos

El coste de máximos locales queda acotado a O(N log N) y conserva la definición escalar exacta. El scratch usa
buckets de potencia de dos y un presupuesto configurable compartido por la instancia de pool inyectada; la
composición productiva debe compartir esa instancia. El presupuesto final permanece pendiente de benchmarks y
no se declara objetivo productivo en esta fase.

## Cuestiones pendientes

Permanecen únicamente las cuestiones separadas de ADR 0017 y las fases posteriores ya planificadas.
