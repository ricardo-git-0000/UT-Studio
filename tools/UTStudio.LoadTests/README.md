# UTStudio.LoadTests

Carga neutral instrumentada. La primera baseline de VM está documentada en el plan; una ejecución aislada no constituye otra baseline aprobada. Perfiles smoke/baseline/soak: 30s/5m/30m. `--duration`, `--warmup` (0s por defecto), `--progress-timeout` y `--cleanup-timeout` aceptan duraciones. El watchdog debe superar el periodo esperado para tasas muy bajas.

Controles operativos: `--source auto|production|experimental`, `--pacing skip-missed|catch-up-bounded`, `--max-catch-up <1..32>` (predeterminado 32), `--telemetry minimal|full`, `--progress normal|quiet` y `--output <ruta.json>`. `--pacing` selecciona exclusivamente el algoritmo de la fuente experimental: una selección explícita con `auto` resuelve a experimental y con `production` se rechaza. Sin `--pacing`, `auto` selecciona producción hasta 100/s y experimental para tasas superiores o `max`. La fuente productiva informa siempre `production-fixed-delay`; `max` informa `unpaced` y rechaza `--pacing`. `quiet` suprime únicamente las líneas periódicas de progreso.

`minimal` conserva demanda programada/ofrecida/omitida, frames generados/aceptados/consumidos/liberados, rent/return, buffers pendientes, continuidad y pérdida funcional, progreso/watchdog, duración activa/cierre, resultado/errores/código de salida y snapshots de CPU, GC y memoria de los extremos. Desactiva correlación secuencia/timestamp, timestamps de latencia visual, histogramas visuales y de proyección, cronometraje de proyección, retrasos detallados de pacing y muestreo periódico de recursos. Proyección, mailbox, publicación y liberación siguen ejecutándose. Los timestamps necesarios para pacing, metadatos y cortes de integridad permanecen.

`full` habilita todas esas mediciones y la serie circular acotada de recursos. La cabecera muestra `telemetry=Minimal detailedPerFrameInstrumentation=disabled` o `telemetry=Full detailedPerFrameInstrumentation=enabled`.

JSON **schemaVersion=4** sustituye el ambiguo `options.pacing` por `options.requestedPacing` (null si no se indicó) y `result.effectivePacing`. Conserva el resto de campos de v3. Producción no tiene rejilla: `GridRate` es null, `Demand.Missed/Pending` son cero y `DiagnosticTargetDeficitFrames` compara de forma diagnóstica con el objetivo sin atribuir omisiones al productor. Las métricas no recopiladas siguen siendo null y la consola usa `unavailable`.

```powershell
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 2048 --rate 100 --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 2048 --rate 1000 --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --source experimental --samples 2048 --rate 1000 `
  --pacing catch-up-bounded --max-catch-up 32 --telemetry full --progress quiet
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 65535 --rate max --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile baseline --source auto --warmup 60s --telemetry full --progress quiet --output performance-results/baseline.json
```

`--rate N` es objetivo, no garantía. En la fuente experimental, `skip-missed` conserva una rejilla y omite slots vencidos; `catch-up-bounded` acumula demanda monotónica y ofrece ráfagas de hasta `max-catch-up`. Solo estos algoritmos administran slots y pueden incrementar `Demand.Missed`. La fuente productiva es fixed-delay: tras producir y entregar espera un periodo completo, por lo que generación y backpressure se suman al periodo y pueden causar deriva. Su déficit respecto al objetivo es diagnóstico, no demanda omitida. `max` mide carga experimental sin pacing, no capacidad sostenible.

Las tasas activas excluyen warmup y drenaje; los contadores de demanda experimental `untilActiveEnd` incluyen inicio/warmup. En Full, los histogramas contienen las últimas 8192 observaciones activas y la serie de recursos las últimas 4096 muestras. La calibración Minimal/Full quedó completada antes de cerrar la baseline y su overhead fue inferior a la variabilidad observada entre repeticiones de la VM.

Ctrl+C solicita cierre ordenado. Códigos: 0 campaña completada con balances; 2 fallo funcional; 3 cancelada; 4 sin progreso/cero frames; 5 cleanup timeout (recursos no confirmados, sin liberación forzada); 64 argumentos inválidos. Limpieza correcta se informa separada de campaña completada. Duración cumplida no demuestra estabilidad estadística.

Semánticas completas en [plan](../../docs/plans/2026-09-16-ascan-performance-baseline.md).

La primera máquina de referencia es `VM-R7700X-4vCPU-8GiB-W11-26200-dotnet10.0.11`. Sus resultados sirven para regresiones repetidas dentro de esa misma VM; no representan una medida absoluta de capacidad del producto.
