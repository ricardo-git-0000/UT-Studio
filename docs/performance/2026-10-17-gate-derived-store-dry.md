# Benchmark diagnóstico Dry — store derivado de puertas

Fecha: 2026-10-17. Commit base: `5dc15933afa0510b482f2309299002ed1e24cffa` con working tree de Fase 4.

## Objetivo y entorno

Smoke diagnóstico de commits RAM completos; no constituye baseline ni objetivo productivo. BenchmarkDotNet
0.15.8, job `Dry`, .NET 10.0.12 x64, Windows 11 en VMware, Intel Core i7-13700H expuesto con cuatro cores.

Comando:

```powershell
dotnet run --project benchmarks/UTStudio.Benchmarks/UTStudio.Benchmarks.csproj -c Release -f net10.0 --no-build -- --filter *GateDerivedStoreBenchmarks* --job Dry
```

## Resultado

| Batches confirmados | Tiempo de la única iteración | Memoria administrada |
| ---: | ---: | ---: |
| 1 | 19,97 ms | 6,27 KiB |
| 64 | 21,10 ms | 251,91 KiB |

El proceso terminó correctamente y ambos escenarios confirmaron todas las revisiones esperadas. Una sola
iteración fría no permite inferir distribución, throughput sostenible ni presupuesto final. La virtualización
y el tiempo de iteración inferior a 100 ms son advertencias explícitas del runner.
