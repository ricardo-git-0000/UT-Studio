# ADR 0005 — Estructura inicial y sección vertical A-Scan

Fecha: 2026-09-09. Estado: Aceptado por el usuario; implementación pendiente.
Trazabilidad: R01–R03, R06, R08 y R13 en [requisitos](../requirements/baseline.md).

## Contexto

El mapa lógico inicial no fijaba los proyectos físicos que debían crearse primero. El usuario aprueba comenzar con una sección vertical: simulador -> sesión -> A-Scan neutral -> ViewModel -> ventana WPF. El modelo visual neutral tiene responsabilidad real desde este incremento.

## Decisión

La estructura inicial constará de siete proyectos productivos, bajo `src/UTStudio.<nombre>/`, y `tests/UTStudio.Core.Tests/`:

| Proyecto UTStudio.* | Responsabilidad | Referencias directas permitidas |
| --- | --- | --- |
| Domain | Semántica, identidades, unidades y reglas UT | Ningún proyecto propio |
| Contracts | Puertos e intercambios neutrales necesarios | Domain |
| Application | Casos de uso y sesión activa | Domain, Contracts |
| Acquisition.Simulator | Fuente sintética determinista | Domain, Contracts |
| Visualization.Core | Modelo y transformación A-Scan neutrales | Domain, Contracts |
| Presentation | ViewModels, estado observable y coordinación visual | Domain, Contracts, Visualization.Core |
| App.Wpf | Vistas, adaptación WPF y composition root | Los seis proyectos neutrales |
| Core.Tests | Pruebas del núcleo y arquitectura | Los seis proyectos neutrales; nunca App.Wpf |

Los proyectos neutrales y Core.Tests tendrán objetivo `net10.0`; App.Wpf, `net10.0-windows`. Application no referencia Visualization.Core: publica datos mediante Contracts y Presentation coordina la transformación neutral. Las referencias a implementaciones de App.Wpf se limitan a composición; vistas y ViewModels no acceden a fuentes concretas.

Contracts se organiza conceptualmente por Acquisition, Application, Presentation y Diagnostics. Solo se incorporan contratos consumidos por el incremento. Los puertos UI locales permanecen en Presentation y se implementan inicialmente en App.Wpf; los modelos A-Scan pertenecen a Visualization.Core. No se crean ciclos ni dependencias UI directas o transitivas en el núcleo. Véase el [grafo y límites](../architecture/overview.md).

No crear todavía SignalProcessing, Storage, GigE, PCIe, Reporting, PLC, Robot, Avalonia ni 3D. Se aplazan también Acquisition.Core, importadores y proyectos WPF separados hasta que tengan responsabilidad real. No se anticipan tiles ni otras modalidades de scan en Visualization.Core. Los informes PDF siguen en el alcance de v1.

## Alternativas consideradas

- Un monolito debilitaría las fronteras de portabilidad y pruebas.
- Crear todos los proyectos del mapa lógico anticiparía responsabilidades ausentes.
- Posponer Visualization.Core dejaría sin frontera propia el A-Scan neutral del primer incremento aprobado.

## Consecuencias y relación con decisiones previas

Concreta la distribución física pendiente en ADR 0001 y en el índice histórico de ADR; mantiene las restricciones de ADR 0001–0004. Core.Tests agrupa suites por responsabilidad y no cubre integración WPF ni hardware. Framework, mocking y CI siguen pendientes de Q10; esta decisión no aprueba paquetes.

El [ADR 0006](0006-session-window-lifecycle.md) fija la propiedad de sesión y el cierre. El [plan](../plans/2026-09-09-first-vertical-increment.md) organiza el trabajo posterior. En esta fase solo se actualiza documentación; no se genera solución, proyectos ni código.

## Pendientes

Contratos mínimos y ownership del consumidor único; semántica de snapshots; representación y renderizado del A-Scan; presupuestos y pruebas/CI. La estrategia compartida de buffers se decidirá antes de introducir varios consumidores, conforme al [pipeline](../architecture/data-pipeline.md).
