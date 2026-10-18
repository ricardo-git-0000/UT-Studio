# Reprocesado de puertas UT — benchmark Dry diagnóstico

Fecha: 2026-10-17. Commit base: `e058433c1e07d63afb3ea7011be36bd81bf81e23`.

## Objetivo

Comprobar que el benchmark de Fase 5 ejecuta el flujo completo de snapshot sellado, enumeración paginada,
evaluación real, commit derivado y cobertura terminal para 2.048/65.535 muestras y bloques 1/64. No establece
baseline, presupuesto productivo ni garantía de latencia.

## Configuración y resultado

BenchmarkDotNet 0.15.8, job `Dry`, .NET 10.0.12, Windows 11 sobre VMware, cuatro trazas sintéticas dispersas,
un stream y un worker histórico. Cada caso tuvo una sola iteración medida.

| Muestras | Bloque | Tiempo diagnóstico | Asignado |
| ---: | ---: | ---: | ---: |
| 2.048 | 1 | 78,86 ms | 181,86 KB |
| 2.048 | 64 | 79,85 ms | 181,79 KB |
| 65.535 | 1 | 77,88 ms | 182,25 KB |
| 65.535 | 64 | 76,30 ms | 205,84 KB |

El job terminó correctamente en los cuatro casos. BenchmarkDotNet advirtió que las iteraciones eran menores
de 100 ms y que la virtualización puede afectar los resultados. Estos números solo prueban ejecución y no deben
usarse para dimensionar producto.

## Pendientes históricos — sustituidos por la matriz operacional posterior

La matriz diagnóstica ampliada original dejó pendientes provider/store lentos, varios streams, live simultáneo,
cancelación, Stop, batches `Duplicate`, memoria máxima nativa y tiempos de drenaje. Las fronteras contractuales
se midieron posteriormente en la ejecución operacional documentada más abajo; memoria máxima nativa y tiempos
de drenaje permanecen fuera del alcance de este benchmark Dry y no constituyen criterios de aceptación funcional.

## Dry ampliado de 2026-10-18

Se ejecutaron 24/24 combinaciones en .NET 10.0.12 sobre VMware: 2.048/65.535 muestras, bloques 1/64/256,
1/4 streams y `StoreOnly`/`ActivateOnComplete`, con 16 trazas por stream. El rango observado fue 83,87–144,78 ms
y 233,49–740,55 KB asignados por operación. Cada invocación verificó cleanup, leases, buffers, scratch, writers
y reservas terminales. BenchmarkDotNet avisó de virtualización y de iteraciones inferiores a 100 ms en diez
casos; por ser `Dry` de una sola iteración, estos valores siguen sin constituir baseline ni objetivo productivo.

## Dry operacional por fronteras públicas — 2026-10-18

`GateReprocessingOperationalBenchmarks` ejecutó 10/10 escenarios autónomos mediante API pública. Las
barreras de provider/store son deterministas: prueban bloqueo, reanudación y drenaje, no una duración de
latencia simulada. El benchmark de Stop mide trabajo histórico detenido en fronteras públicas reproducibles;
no afirma observar la entrada interna a `GateEvaluator.Evaluate`.

| Escenario | Tiempo | Asignado |
| --- | ---: | ---: |
| Activate, varios streams | 117,38 ms | 260,27 KB |
| Cancel, provider bloqueado | 75,41 ms | 29,50 KB |
| Cancel, store bloqueado | 84,88 ms | 159,53 KB |
| Duplicate, varios bloques | 107,29 ms | 291,40 KB |
| Normal, 2.048, bloque 1, un stream | 91,27 ms | 165,58 KB |
| Normal, 65.535, bloque 256, cuatro streams | 116,87 ms | 260,22 KB |
| Provider bloqueado + live | 125,49 ms | 185,25 KB |
| Stop durante lectura pública | 68,04 ms | 29,70 KB |
| Stop durante store público | 85,20 ms | 181,84 KB |
| Store bloqueado + live | 125,83 ms | 184,49 KB |

Cada invocación construye un diagnóstico estructurado con dimensiones, lifecycle, identidades,
batches nuevos/Duplicate, revisión, vista y checksum de recursos. Antes de devolver valida leases, buffers,
scratch, workers, callbacks, writers y reservas; todos terminaron balanceados. BenchmarkDotNet advirtió de
virtualización y de cinco iteraciones inferiores a 100 ms. Los resultados son diagnósticos Dry, no baseline ni
objetivo productivo.
