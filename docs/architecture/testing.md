# Estrategia de pruebas

Trazabilidad: R01–R14. Estrategia de producto; alcance inicial en [plan](../plans/2026-09-09-first-vertical-increment.md). MSTest 4.0.2 aprobado/presente desde 2026-09-10; mocking y CI abiertos (Q10). No instalar paquetes ni crear proyectos en esta actualización.

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
Cuando exista código, ejecutar build y pruebas pertinentes después de cambios. En esta actualización solo se comprueban documentación, enlaces, consistencia y ausencia de cambios de código. Las pruebas del plan sobre canal lleno, ownership, cierre MainWindow, 30 Hz y métricas a 5 Hz no se consideran ejecutadas.
