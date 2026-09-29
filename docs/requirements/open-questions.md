# Cuestiones abiertas

No asumir respuestas ni bloquear la fase documental por estas incógnitas. Resolverlas antes de implementar la parte afectada.

| ID | Cuestión / evidencia necesaria | Responsable |
| --- | --- | --- |
| Q01 | Paquetes GigE: framing, secuencias, reloj, orden, integridad, MTU y control de flujo | ut_acquisition |
| Q02 | API PCIe: documentación, capacidades, memoria/DMA, cancelación, errores, licencia y plataformas | ut_acquisition |
| Q03 | Leyes focales habituales y máximo operativo; muestras, PRF, simultaneidad convencional y configuraciones reales | ut_domain_specialist |
| Q04 | Política del equipo ante saturación; pausa/parada segura, buffers y margen de caudal medido | ut_acquisition |
| Q05 | Paso espacial por eje, geometría, coordenadas y sincronización posición-tiempo; significado operativo de D-Scan | ut_domain_specialist |
| Q06 | Formato nativo, bloques, compresión, integridad, durabilidad y ventana recuperable; crudo/procesado a conservar | ut_storage_formats |
| Q07 | Formatos externos prioritarios y archivos de ejemplo autorizados | ut_storage_formats |
| Q08 | Informe: contenido, PDF/A, firmas, imágenes, plantilla corporativa y licencia | pdf_reporting |
| Q09 | Benchmarks CPU/memoria pendientes; A-Scan y B-Scan usan límites de 30 Hz distintos y configurables. GPU y ajustes de índice/tiles siguen abiertos (ADR 0008/0013) | ut_visualization |
| Q10 | MSTest 4.0.2 aprobado/presente desde 2026-09-10; mocking, CI, hardware/duración de benchmarks y paquetes adicionales pendientes | quality_reviewer |
| Q11 | Instalación, actualización y plataformas Linux realmente requeridas | solution_architect |
| Q12 | Primera secundaria BScanWindow, propiedad, presupuesto y cierre resueltos en ADR 0013; otras inspecciones/ventanas concurrentes permanecen abiertas | wpf_mvvm_specialist |
| Q13 | Fan-out visual A/B por copia propia resuelto en ADR 0013; ownership, orden y backpressure de procesamiento/persistencia siguen pendientes | solution_architect |
| Q14 | Remuestreo B-Scan: aprobar malla fuente, política estricta de máscara, Keys `a=-0.5`, Lanczos `a=3`, persistencia y objetivos/hardware de benchmark; decidir si GP/Kriging queda prohibido sobre índice ordinal y definir después geometría, modelo e incertidumbre física | ut_visualization / ut_domain_specialist |

La representación binaria de rectificadas reales sigue abierta; inicialmente solo RF bipolar `ReadOnlyMemory<short>`. Véanse [ADR 0007](../adr/0007-frame-source-ownership.md), [ADR 0008](../adr/0008-latest-only-visual-delivery.md) y [ADR 0006 revisado](../adr/0006-session-window-lifecycle.md).

Q01–Q04 condicionan la adquisición real. Q06 condiciona el formato persistente. Q08 condiciona la selección PDF. No incorporar NuGet antes de justificarlo y obtener aprobación.
