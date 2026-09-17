# UTStudio.LoadTests

Carga neutral instrumentada; no representa una baseline aprobada. Perfiles smoke/baseline/soak: 30s/5m/30m. `--duration`, `--warmup` (0s por defecto), `--progress-timeout` y `--cleanup-timeout` aceptan duraciones. El watchdog debe superar el periodo esperado para tasas muy bajas.

Controles operativos: `--source auto|production|experimental`, `--telemetry minimal|full`, `--progress normal|quiet` y `--output <ruta.json>`. `auto` selecciona la fuente productiva hasta 100/s y la experimental para tasas superiores o sin pacing. `production` rechaza tasas superiores a 100/s y `max`. Telemetría `minimal` conserva el resumen pero no retiene ni exporta la serie periódica; `full` conserva la serie circular. `quiet` suprime las líneas periódicas, no el resumen final ni los errores. La salida JSON incluye configuración, resultados, balances y código de salida.

```powershell
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 2048 --rate 100 --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 2048 --rate 1000 --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 65535 --rate max --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile baseline --source auto --warmup 60s --telemetry full --progress quiet --output performance-results/baseline.json
```

`--rate N` es objetivo ofrecido. Se publican tasas objetivo, rejilla, ofrecida, aceptada y consumida; los deadlines omitidos son demanda no atendida, no pérdida UT. Task.Delay no garantiza 1 ms. Hasta 100/s se conserva simulador/generador RF/pool productivos; por encima se usa fuente/generador LCG/pool experimentales. `max` significa sin pacing, no capacidad sostenible.

Las tasas activas excluyen warmup y drenaje; los contadores de demanda `untilActiveEnd` incluyen inicio/warmup. Los histogramas contienen últimas 8192 observaciones activas; la serie de memoria/CPU conserva últimas 4096 muestras con duración real. CPU y working set máximos son muestreados. Memoria administrada es estimación, no heap reservado. Locks, timestamps, atomics y wrappers de instrumentación forman parte del coste de la carga. El contraste de overhead sigue pendiente antes de baseline.

Ctrl+C solicita cierre ordenado. Códigos: 0 campaña completada con balances; 2 fallo funcional; 3 cancelada; 4 sin progreso/cero frames; 5 cleanup timeout (recursos no confirmados, sin liberación forzada); 64 argumentos inválidos. Limpieza correcta se informa separada de campaña completada. Duración cumplida no demuestra estabilidad estadística.

Semánticas completas en [plan](../../docs/plans/2026-09-16-ascan-performance-baseline.md).

La primera máquina de referencia es `VM-R7700X-4vCPU-8GiB-W11-26200-dotnet10.0.11`. Sus resultados sirven para regresiones repetidas dentro de esa misma VM; no representan una medida absoluta de capacidad del producto.
