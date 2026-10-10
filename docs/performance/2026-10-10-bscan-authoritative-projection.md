# B-Scan authoritative projection — Dry diagnostics

Date: 2026-10-10. Environment: Windows 11 VM (VMware), Intel i7-13700H exposed as four logical
processors, .NET 10.0.12, BenchmarkDotNet 0.15.8. These single-iteration Dry measurements are
diagnostic, not throughput guarantees.

Reproduction:

```text
dotnet run -c Release -f net10.0 --project benchmarks/UTStudio.Benchmarks/UTStudio.Benchmarks.csproj -- --job Dry --filter *BScanFullRasterBenchmarks* *BScanIncrementalRasterBenchmarks* *BScanReconciliationLifecycleBenchmarks*
```

## Results

| Scenario | Dry mean | Managed allocation |
| --- | ---: | ---: |
| Full 256, None | 39.991 ms | 160.55 KiB |
| Full 1024, None | 89.750 ms | 232.55 KiB |
| Full 1024, Lanczos | 227.247 ms | 490.63 KiB |
| Incremental 1 | 3.743 ms | 52.05 KiB |
| Incremental 8 | 3.064 ms | 177.23 KiB |
| Incremental 33 | 4.809 ms | 618.33 KiB |
| Incremental 100 | 5.059 ms | 1845.64 KiB |
| Recuperación de notificación omitida | 6.278 ms | 130,088 B |
| StopAdmission + drain + freeze | 2.249 ms | 840 B |

El raster full también completó Nearest, Bilinear, Bicubic y Lanczos en 256/1024. No existe caché ni
índice RF derivado retenido: el candidato se reserva primero, cada consulta contiene como máximo 256
posiciones y sus leases se liberan antes de leer la página siguiente. El scratch neutral y los owners
del raster comparten el ledger físico de 16 MiB. Las regresiones confirman cuatro páginas exactas para
1.024×2.048 y memoria visual independiente de comparar 2.048 frente a 65.535 muestras.

## Load smoke

El smoke productivo de cinco segundos (`2048` muestras, objetivo diagnóstico `100/s`) atravesó
`ApplicationSession`, store, `BScanIngress` real y A-Scan: 234 producidos/consumidos/liberados,
cero outstanding, barrera confirmada, cleanup correcto y cero errores. La VM sostuvo 46.71 frames/s;
no constituye certificación del objetivo.
