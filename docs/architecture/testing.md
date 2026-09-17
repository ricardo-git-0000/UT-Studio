# Estrategia de pruebas

Trazabilidad: R01–R14. Estrategia de producto; alcance inicial en [plan](../plans/2026-09-09-first-vertical-increment.md). MSTest 4.0.2 está aprobado y presente desde 2026-09-10; mocking y CI siguen abiertos (Q10). BenchmarkDotNet 0.15.8 está autorizado exclusivamente en `UTStudio.Benchmarks`.

| Nivel | Casos y evidencia esperada |
| --- | --- |
| Dominio/procesamiento | IDs y unidades distintos, límites, geometría, señales conocidas, gates y medidas con tolerancias justificadas |
| Contratos | Misma batería para simulador, reproducción, GigE y PCIe; ciclo de vida, cancelación, secuencias y errores |
| Aplicación | Inicio/parada/fallo y rollback; único lector drena mientras se cancela productor bloqueado; MainWindow solicita salida, secundarias futuras no detienen sesión |
| Arquitectura | Grafo acíclico; referencias WPF solo en .Wpf; núcleo/Presentation sin WPF/Avalonia, incluso transitivas; APIs sin tipos UI; ningún ViewModel referencia otro |
| Concurrencia/memoria | Consumidores lentos, canales llenos, descarte UI, error y cancelación; cada buffer se devuelve una vez, sin uso posterior ni crecimiento ilimitado |
| Almacenamiento | Truncar cabecera, payload, índice y confirmación; corrupción interna; recuperar exactamente lo declarado válido y diagnosticar pérdidas |
| Lectura/formatos | Acceso regional con memoria limitada y medición de bloques leídos; índices/previews reconstruibles; versiones incompatibles controladas |
| Rendimiento | Validación aritmética, carga sostenida, presión de GC, bytes en colas, latencia y caudal de escritura con metadatos |
| UI/PDF | Tasa visual independiente, geometría/tiles neutrales, varias ventanas y liberación de suscripciones; PDF según requisitos por definir |
| Hardware | Integración real separada de pruebas deterministas; transporte, SDK y respuesta a saturación con equipo disponible |

El simulador inicial tiene semilla y reloj controlable. Para incrementos posteriores se prevén fallos reproducibles: pérdida, duplicado, reordenación, imagen incompleta, pausa, desconexión y ráfagas. Evitar tests basados exclusivamente en sleeps o mediciones sin hardware/configuración registrados.
Generar inspecciones sintéticas grandes fuera de Git durante las pruebas; fixtures versionados pequeños con procedencia, semilla, tamaño y propósito documentados. Umbral de fixture pendiente de acordar; revisar manualmente tamaño y contenido.
Medir presupuestos de memoria, CPU, FPS, duración de soak y margen de caudal antes de fijar criterios de rendimiento: no inventar SLA. Contabilizar frames recibidos, persistidos y discontinuidades.

El [plan de rendimiento y estabilidad A-Scan](../plans/2026-09-16-ascan-performance-baseline.md) separa microbenchmarks, carga del pipeline y pruebas prolongadas. `UTStudio.Benchmarks` contiene los microbenchmarks; `UTStudio.LoadTests` es un ejecutable neutral separado de MSTest. Las campañas se lanzan deliberadamente y nunca mediante `dotnet test`; la suite solo ejecuta pruebas deterministas de sus componentes y contratos.

Las señales y fixtures son sintéticos. Los fixtures grandes, JSON de resultados, capturas, trazas y artefactos permanecen fuera de Git; cualquier fixture pequeño versionado requiere procedencia, semilla, tamaño y revisión. La baseline disponible pertenece únicamente a `VM-R7700X-4vCPU-8GiB-W11-26200-dotnet10.0.11` y sirve para regresiones con la misma configuración. Sus presupuestos son umbrales provisionales de aviso, no requisitos absolutos ni fallos de CI. Siguen pendientes baseline en hardware físico, transporte GigE real, transporte PCIe real y soak prolongado.
