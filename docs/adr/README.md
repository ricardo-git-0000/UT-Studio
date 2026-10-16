# Registro de decisiones

Aceptado identifica decisiones aprobadas, sin autorizar paquetes ni implementación. ADR 0006 revisado sustituye su política anterior de cierre.

- [0001-portable-architecture](0001-portable-architecture.md): Núcleo portable y adaptación WPF.
- [0002-bounded-pipeline](0002-bounded-pipeline.md): Canales acotados y saturación observable.
- [0003-incremental-storage](0003-incremental-storage.md): Persistencia incremental recuperable.
- [0004-neutral-reporting](0004-neutral-reporting.md): Informe neutral y decisiones tecnológicas diferidas.

- [0005-initial-solution-structure](0005-initial-solution-structure.md): Estructura aceptada; scaffolding existente.
- [0006-session-window-lifecycle](0006-session-window-lifecycle.md): MainWindow solicita salida ordenada; secundarias futuras no detienen sesión.
- [0007-frame-source-ownership](0007-frame-source-ownership.md): Contrato RF y ownership exclusivo de muestras short.
- [0008-latest-only-visual-delivery](0008-latest-only-visual-delivery.md): Mailbox latest-only, snapshot independiente y cadencias.
- [0009-reusable-scan-visualization-controls](0009-reusable-scan-visualization-controls.md): Renderizadores de scan específicos y reutilizables por plataforma.
- [0010-ascan-manual-cursor-measurements](0010-ascan-manual-cursor-measurements.md): Ejes físicos y cursores manuales aproximados sobre puntos visuales A-Scan.
- [0011-shared-physical-time-viewport](0011-shared-physical-time-viewport.md): Zoom temporal neutral compartible entre A-Scan y futuros B-Scan.
- [0012-shared-scan-time-navigation](0012-shared-scan-time-navigation.md): navegación temporal reutilizable, identidad latest-only, ciclo de vida y sincronización bidireccional entre scans.
- [0013-bscan-bounded-live-visualization](0013-bscan-bounded-live-visualization.md): B-Scan acotado, selección exacta, entrega incremental y ventana secundaria; su fan-out RF original está marcado como histórico y sustituido.
- [0014-bscan-selectable-resampling](0014-bscan-selectable-resampling.md): decisión aceptada e implementada de remuestreo visual seleccionable y máscara/procedencia 2D; GP/Kriging continúa futuro y separado.
- [0015-authoritative-inspection-data-store](0015-authoritative-inspection-data-store.md): autoridad única neutral implementada en RAM para adquisición y proyecciones A/B; persistencia permanece pendiente.
- [0016-reusable-complete-scan-components](0016-reusable-complete-scan-components.md): propuesta pendiente de aprobación para componentes WPF completos por scan con comunes mínimos; no autoriza código ni C-Scan.
- [0017-neutral-ut-gate-processing](0017-neutral-ut-gate-processing.md): propuesta pendiente de aprobación para procesamiento neutral, determinista y acotado de puertas sobre RF confirmada; no autoriza implementación ni C-Scan.

Renderizadores C/S/D-Scan, paquetes adicionales y benchmarks siguen pendientes. Véanse el [plan vertical](../plans/2026-09-09-first-vertical-increment.md), el [plan B-Scan](../plans/2026-09-22-bscan-first-increment.md), el [plan de remuestreo B-Scan](../plans/2026-09-29-bscan-interpolation-modes.md), el [plan del store](../plans/2026-10-05-inspection-data-store.md), la [propuesta de componentes completos](../plans/2026-10-13-reusable-scan-components.md), el [plan de puertas UT](../plans/2026-10-16-ut-gate-processing.md) y las [cuestiones](../requirements/open-questions.md).
