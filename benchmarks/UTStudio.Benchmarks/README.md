# UTStudio.Benchmarks

Microbenchmarks aislados del pipeline A-Scan. Usa BenchmarkDotNet 0.15.8 únicamente en este proyecto y `MemoryDiagnoser` para asignaciones y colecciones GC. Los datos se preparan fuera de la región medida. Los resultados son experimentales; no son pruebas ni presupuestos aprobados.

```powershell
dotnet run -c Release --project benchmarks/UTStudio.Benchmarks
dotnet run -c Release --project benchmarks/UTStudio.Benchmarks -- --filter '*Projection*' --job short
```

Los casos interesados realizan una publicación preparatoria y después usan un `TimeProvider` congelado para excluir temporización y publicación recurrente. Cada benchmark mide solo la operación indicada; la publicación/observación real se mide en la herramienta de carga.

El setup usa la barrera diagnóstica interna de `AScanVisualDelivery`. La barrera confirma una espera física pendiente del worker, generación vigente, ausencia de proyecciones en curso y finalización de pumps/callbacks preparatorios. El reloj congelado excluye temporización y publicación recurrente.

Los casos están separados: Accept sin interés; una proyección hacia mailbox vacío; proyección con sustitución latest-only ya sembrada; y lectura de estadísticas. `IterationSetup` fuerza una invocación por iteración, necesaria para que el caso de mailbox vacío conserve esa precondición. El cleanup diagnóstico cancela y observa worker, callbacks, pumps y proyecciones. Dry comprueba ejecución, no estabilidad estadística ni constituye baseline.
