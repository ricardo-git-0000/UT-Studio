# UTStudio.LoadTests

Carga neutral instrumentada; no representa una baseline aprobada. Perfiles smoke/baseline/soak: 30s/5m/30m. `--duration`, `--warmup` (0s por defecto), `--progress-timeout` y `--cleanup-timeout` aceptan duraciones. El watchdog debe superar el periodo esperado para tasas muy bajas.

Controles operativos: `--source auto|production|experimental`, `--pacing skip-missed|catch-up-bounded`, `--max-catch-up <1..32>` (predeterminado 32), `--telemetry minimal|full`, `--progress normal|quiet` y `--output <ruta.json>`. `auto` selecciona la fuente productiva hasta 100/s y la experimental para tasas superiores, `catch-up-bounded` o `max`. La fuente productiva rechaza `catch-up-bounded`; `max` rechaza `--pacing` explícito. `quiet` suprime únicamente las líneas periódicas de progreso: conserva instrumentación, watchdog, resumen final y errores.

`minimal` conserva demanda programada/ofrecida/omitida, frames generados/aceptados/consumidos/liberados, rent/return, buffers pendientes, continuidad y pérdida funcional, progreso/watchdog, duración activa/cierre, resultado/errores/código de salida y snapshots de CPU, GC y memoria de los extremos. Desactiva correlación secuencia/timestamp, timestamps de latencia visual, histogramas visuales y de proyección, cronometraje de proyección, retrasos detallados de pacing y muestreo periódico de recursos. Proyección, mailbox, publicación y liberación siguen ejecutándose. Los timestamps necesarios para pacing, metadatos y cortes de integridad permanecen.

`full` habilita todas esas mediciones y la serie circular acotada de recursos. La cabecera muestra `telemetry=Minimal detailedPerFrameInstrumentation=disabled` o `telemetry=Full detailedPerFrameInstrumentation=enabled`.

JSON **schemaVersion=3** añade de forma aditiva pacing, límite de recuperación, tasas explícitas y métricas de demanda/ráfagas, conservando los campos de v2. Representa explícitamente con `null` las métricas no recopiladas: `VisualDeliveryLatencyMicroseconds`, `ProjectionMicroseconds`, `CorrelationMisses`, `CorrelationOverwrites`, `MaximumWorkingSetBytes`, `MaximumCpuPercent`, `resourceSamples`, `TotalResourceSamples` y ambos retrasos de `Demand` en Minimal. En consola se muestran como `unavailable`. Los histogramas Full sin observaciones también son `null/unavailable`, nunca ceros ni NaN. `ManagedBytesAtActiveStart/End` y `WorkingSetBytesAtActiveStart/End` conservan los extremos de la ventana (null si no comenzó); CPU media y GC activos se calculan con sus cortes de inicio/fin. Memoria post-cleanup se informa por separado. Los máximos de recursos requieren Full; no se infieren a partir de los extremos de Minimal.

```powershell
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 2048 --rate 100 --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 2048 --rate 1000 --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --source experimental --samples 2048 --rate 1000 `
  --pacing catch-up-bounded --max-catch-up 32 --telemetry full --progress quiet
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile smoke --samples 65535 --rate max --duration 2s
dotnet run -c Release --project tools/UTStudio.LoadTests -- --profile baseline --source auto --warmup 60s --telemetry full --progress quiet --output performance-results/baseline.json
```

`--rate N` es objetivo ofrecido. Se publican tasas objetivo, rejilla, ofrecida, aceptada y consumida, además de demanda programada, ofrecida, recuperada, definitivamente omitida y pendiente; las omisiones de pacing no son pérdida UT. `skip-missed` conserva la rejilla anterior y sirve para observar la limitación del planificador. `catch-up-bounded` acumula demanda monotónica y ofrece ráfagas de hasta `max-catch-up` para entregar la carga media solicitada. Las ráfagas no reproducen una llegada perfectamente uniforme: sirven para validar la capacidad media del pipeline. El generador experimental forma parte del coste medido. Pool y canal siguen acotados y pueden bloquear dentro de una ráfaga; ese retraso permanece observable. No se usa busy-spin ni se cambia la resolución global de temporizadores de Windows. `max` mide carga sin pacing, no capacidad sostenible.

Las tasas activas excluyen warmup y drenaje; los contadores de demanda `untilActiveEnd` incluyen inicio/warmup. En Full, los histogramas contienen últimas 8192 observaciones activas; la serie de memoria/CPU conserva últimas 4096 muestras con duración real. CPU y working set máximos son muestreados. Memoria administrada es estimación, no heap reservado. Minimal conserva el coste de los controles de integridad; comparar Minimal/Full permite estudiar el coste adicional de las sondas detalladas. El contraste de overhead sigue pendiente antes de baseline.

Ctrl+C solicita cierre ordenado. Códigos: 0 campaña completada con balances; 2 fallo funcional; 3 cancelada; 4 sin progreso/cero frames; 5 cleanup timeout (recursos no confirmados, sin liberación forzada); 64 argumentos inválidos. Limpieza correcta se informa separada de campaña completada. Duración cumplida no demuestra estabilidad estadística.

Semánticas completas en [plan](../../docs/plans/2026-09-16-ascan-performance-baseline.md).

La primera máquina de referencia es `VM-R7700X-4vCPU-8GiB-W11-26200-dotnet10.0.11`. Sus resultados sirven para regresiones repetidas dentro de esa misma VM; no representan una medida absoluta de capacidad del producto.
