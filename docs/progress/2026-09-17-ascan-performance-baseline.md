# Cierre de la primera baseline A-Scan

Fecha: 2026-09-17.

## Objetivo

Cerrar documentalmente la primera referencia reproducible de rendimiento del pipeline A-Scan convencional, preservando sus límites de interpretación y dejando explícito el trabajo que aún no está cubierto.

## Resultado

Se acepta como referencia de regresión local la campaña ejecutada en `VM-R7700X-4vCPU-8GiB-W11-26200-dotnet10.0.11`, plan de energía **Equilibrado**, Release y sin depurador. El protocolo fue de tres procesos independientes por caso, cada uno con un minuto de calentamiento y cinco minutos activos, telemetría `Full` y progreso `Quiet`.

Resultados consolidados (mínimo–máximo de tres repeticiones):

| Caso | Tasa consumida | CPU activa | Máx. working set | Proyección p95 | Entrega visual p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 2.048 muestras, experimental, objetivo 1.000/s, pacing `catch-up-bounded` | 999,999–1.000,002/s | 25,68–30,31 % | 54,55–54,91 MiB | 13,4–13,7 µs | 1.079,5–1.086,3 µs |
| 65.535 muestras, experimental, `max` sin pacing | 15.522,75–16.273,49/s | 55,78–57,06 % | 55,83–56,02 MiB | 69,5–71,5 µs | 169,1–184,7 µs |

En todas las repeticiones hubo cero errores, balances completos, cero buffers pendientes y barrera de liberación confirmada. Los casos experimentales consolidados terminaron además con demanda omitida y pendiente igual a cero. Quedaron confirmadas la propiedad de buffers y las barreras de cierre bajo este protocolo.

Las referencias productivas anteriores, con una sola ejecución por caso, abarcaron 31,169–31,175/s para objetivos de 50/s y 60,992–61,084/s para objetivos de 100/s. Mantuvieron integridad y limpieza. Su cadencia efectiva era y sigue siendo `production-fixed-delay`: generación y backpressure se suman al periodo. Los slots que los JSON históricos llamaron omitidos procedían de una rejilla diagnóstica externa; no fueron demanda gestionada u omitida por el productor. Se conservan como cadencia observada y déficit diagnóstico bajo VMware, no como validación de `skip-missed` ni evidencia de pérdida UT.

La observación de **15.677,95/s** en la primera ejecución `max` no es capacidad certificada del producto. `max` usa una fuente, un generador LCG y un pool experimentales, y mide carga sin pacing; no representa hardware ni transporte reales y no autoriza extrapolación.

## Decisiones confirmadas

- Esta VM se usa únicamente para detectar regresiones al repetir exactamente la misma configuración y protocolo.
- El pacing de la campaña a 1.000/s es `catch-up-bounded`; `max` permanece sin pacing.
- La calibración Minimal/Full quedó completada antes del cierre; su overhead fue inferior a la variabilidad observada entre repeticiones de la VM.
- Los JSON de resultados continúan como artefactos locales ignorados y no forman parte de la documentación versionada.
- Los presupuestos añadidos al [plan](../plans/2026-09-16-ascan-performance-baseline.md) son umbrales provisionales de aviso. No son requisitos absolutos, garantías de producto, criterios de aceptación ni fallos de CI.

## Archivos modificados

- `docs/plans/2026-09-16-ascan-performance-baseline.md`: estado, protocolo, resultados consolidados, rangos anteriores, límites, avisos provisionales y pendientes.
- `docs/progress/2026-09-17-ascan-performance-baseline.md`: este resumen de cierre.

El cierre original no modificó código, proyectos, paquetes ni resultados JSON. La corrección posterior de semántica de pacing modificó únicamente LoadTests, pruebas Performance y esta documentación; no cambió la fuente productiva ni añadió paquetes.

## Pruebas y validaciones

Para el cierre original se revisaron los resultados locales sin añadirlos al control de versiones. La corrección posterior se valida mediante formato limitado, build Release, suite completa, smokes diagnósticos mínimos de las cuatro etiquetas efectivas, JSON schema 4, enlaces y `git diff --check`; los resultados concretos se registran en la entrega de la tarea y no se versionan.

## Riesgos y límites

- VMware y el plan Equilibrado introducen variabilidad y limitan el pacing productivo.
- La instrumentación `Full` forma parte del coste observado.
- Los rangos proceden solo de esta máquina y de tres repeticiones de los dos casos consolidados.
- Los valores de `max` pertenecen a componentes experimentales y no certifican capacidad del producto.
- Un aviso futuro exige investigar y repetir; no demuestra por sí solo una regresión ni debe romper CI.

## Cuestiones pendientes

- baseline en hardware físico;
- GigE real;
- PCIe real;
- phased array con leyes focales;
- almacenamiento sin pérdida;
- runner WPF/DPI;
- soak prolongado;
- presupuestos por transporte y configuración.
