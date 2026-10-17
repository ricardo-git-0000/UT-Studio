# Fase 3 — orquestación post-commit de puertas UT

Fecha: 2026-10-17.

## Objetivo y resultado

Se implementó la Fase 3 de ADR 0017: señal sample-free posterior al commit, reconciliación lossless por
`InspectionRevision`, evaluación neutral con lease único y entrega transitoria sin persistencia. El commit RF
permanece independiente del resultado de procesamiento.

## Decisiones confirmadas

- El catálogo autoritativo registra un cambio por commit atómico: `Trace` o `GapOnly`.
- La admisión ocurre al abrir la run; las señales posteriores solo coalescen el watermark.
- El plan queda fijado por épocas con revisión efectiva explícita.
- `FinalRevision` sellada es la única frontera de completitud final.
- El sink recibe un batch inmutable prestado durante un callback esperado y no lo retiene.
- No existe store derivado, durabilidad ni reprocesado histórico en esta fase.

## Archivos modificados

Contratos de inspección y procesamiento, store RAM autoritativo, `UTStudio.Processing.Core`,
`ApplicationSession`, composición productiva WPF, pruebas y documentación de ADR/diseño/pipeline/plan.

## Validación

La matriz ejecutable de 73 escenarios queda enlazada desde el plan. La solución Release compiló sin warnings;
Processing pasó 92/92, Core completa pasó 667/667 tres veces y la pasada posterior a la última regresión pasó
668/668. Las carreras sensibles pasaron 50 repeticiones
y la integración productiva pasó 20. El smoke real cubrió seis combinaciones de 2.048/65.535 muestras,
50/100/1.000 trazas ofrecidas, uno/varios streams y planes independiente/cadena, incluidas dos runs sucesivas.
BenchmarkDotNet Dry ejecutó las seis combinaciones propias del orquestador; son diagnóstico, no baseline.

WPF completa obtuvo 134/134 en dos de tres repeticiones. La restante reprodujo una carrera intermitente ajena a
Fase 3 en `IntegratedSimulatorSnapshotDrawsWithoutPerPointVisuals`; su repetición aislada pasó. Compilación y la
prueba nueva de composición productiva pasaron. También pasaron formato limitado, enlaces Markdown, UTF-8
estricto y `git diff --check`; no aparecieron paquetes, binarios/resultados no ignorados ni patrones de secretos.

Defectos productivos corregidos durante la matriz: un reintento `NotAvailable` contaba varias admisiones para
una identidad (se fijaron revisión y época pendientes); una run fallida podía volver a procesarse desde un pulso
ya encolado (se excluyen runs fallidas); un fallo de sink se atribuía a evaluación por el momento de decremento
del callback (se separó la frontera); y una excepción de `Dispose` del lease dejaba el contador activo sin
decrementar (el fin de ownership se contabiliza en `finally`). La revisión final añadió regresiones y correcciones
para finalidad inmutable, retiro fallido posterior al cleanup, `InvalidTrace` sin leer muestras, watermark
monotónico, plan efectivo por run, ventana productiva completa y drain intentado aun si falla la solicitud final.
También se hizo mutuamente excluyente la terminalización frente a un callback que devuelve `Accepted` después
de un `Dispose` excepcional, incluso si ese callback ignora la cancelación.
Cada caso conserva regresión determinista.

## Riesgos y pendientes

P1, P2 y P3 de esta tarea quedan cerrados. La única incidencia de validación es la carrera WPF intermitente
descrita arriba, fuera del código y alcance de procesamiento modificado.

El sink transitorio descarta los batches aceptados deliberadamente; Fase 4 incorporará el store derivado RAM.
Persistencia, reprocesado general, editor A-Scan, C-Scan, PA real y presupuesto final tras benchmarks continúan
fuera de alcance.
