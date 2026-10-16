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
| Q13 | El fan-out RF A/B de ADR 0013 fue sustituido por el store autoritativo y notificaciones sin muestras de ADR 0015; ADR 0017 resuelve ownership, orden y drain del procesamiento de puertas; la persistencia general sigue pendiente | solution_architect |
| Q14 | Remuestreo B-Scan resuelto por ADR 0014: malla, máscara estricta, Keys `a=-0.5`, Lanczos `a=3`, selector y persistencia están aprobados e implementados. Siguen abiertos el hardware, metodología y objetivos para convertir diagnósticos en gates, y una futura decisión separada sobre geometría, modelo e incertidumbre física de GP/Kriging | ut_visualization / ut_domain_specialist |
| Q15 | Persistencia futura: tamaño de chunk persistente, contenedor/formato o base de datos, compresión, checksum, memoria mapeada, política de flush/durabilidad y respuesta definitiva ante disco lento | ut_storage_formats / ut_acquisition |
| Q16 | Identidad hardware/PA: ámbito de `Sequence`, agrupación de trigger/imagen, relaciones canal–elemento–beam–ley y layout phased-array | ut_domain_specialist / ut_acquisition |
| Q17 | Señal y posición: encoding/unidades de rectificada, crudo/derivados a conservar, encoder, sistema de coordenadas, calibración y calidad | ut_domain_specialist / ut_processing |
| Q18 | C-Scan posterior: ADR 0017 resuelve las puertas y capas iniciales; quedan geometría/celdas/tiles, multirresolución y gates de rendimiento sobre hardware acordado | ut_processing / ut_visualization |
| Q20 | Presupuesto final de procesamiento de puertas después de benchmarks reproducibles sobre hardware y configuraciones acordados | ut_processing / quality_reviewer |
| Q21 | Persistencia del store derivado de puertas: formato, durabilidad, recuperación, retención y GC | ut_storage_formats / solution_architect |

La representación binaria de rectificadas reales sigue abierta; inicialmente solo RF bipolar `ReadOnlyMemory<short>`. Véanse [ADR 0007](../adr/0007-frame-source-ownership.md), [ADR 0008](../adr/0008-latest-only-visual-delivery.md) y [ADR 0006 revisado](../adr/0006-session-window-lifecycle.md).

Q01–Q04 condicionan la adquisición real. Q06 y Q15–Q17 condicionan las fases persistentes y PA;
Q18 condiciona C-Scan. Q08 condiciona la selección PDF. El [ADR 0015](../adr/0015-authoritative-inspection-data-store.md)
no resuelve estas cuestiones sin evidencia. No incorporar NuGet antes de justificarlo y obtener aprobación.

## Cuestiones resueltas

| ID | Resolución | Evidencia |
| --- | --- | --- |
| Q19 | Resuelta para el primer incremento: quedan fijadas las quince decisiones de ventanas, dominios, detectores, tiempo, calidad, reproducibilidad, Stop, store derivado RAM, capas, límites, identidad y edición A-Scan | [ADR 0017](../adr/0017-neutral-ut-gate-processing.md) y [diseño](../architecture/ut-gate-processing-design.md) |

Q19 conserva como cuestiones separadas únicamente: encoding persistente de señal rectificada (Q17),
presupuesto final tras benchmarks (Q20), persistencia del store derivado (Q21) y requisitos PA reales (Q16).
