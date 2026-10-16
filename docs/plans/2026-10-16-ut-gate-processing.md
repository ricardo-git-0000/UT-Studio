# Plan aprobado por fases — procesamiento neutral de puertas UT

Fecha: 2026-10-16. Estado: **aprobado; implementación no iniciada**. ADR: [0017](../adr/0017-neutral-ut-gate-processing.md).
Diseño: [procesamiento neutral](../architecture/ut-gate-processing-design.md).

## Propiedad y alcance

Fase 0 es exclusivamente documental. El agente principal es único escritor; revisores especializados son
solo lectura. No hay código, UI, C-Scan, persistencia, proyecto nuevo ni paquete NuGet. Las fases posteriores
conservarán un propietario exclusivo por archivo/módulo y requieren autorización de implementación.

## Fase 0 — contrato documental y aprobación

Entregables: ADR aceptado, modelo/versionado, matriz de detectores, DAG, API conceptual, pipeline/ownership,
presupuesto, pruebas/benchmarks, integración A/C, riesgos y decisiones aceptadas. Gate: enlaces y coherencia
revisados; árbol sin stage/commit; ADR aceptado y Q19 resuelta.

## Fase 1 — contratos y compilador

Crear, tras autorización de implementación, contratos neutrales inmutables y `GatePlanCompiler`. Cubrir
IDs/revisiones, parámetros tipados, mínimo válido de tres puertas, 8 puertas predeterminadas, máximo 16,
validación, DAG y orden topológico determinista. Sin evaluator, store ni UI.

Gate: ejemplos de tres independientes, A→B→C, A→{B,C}, dependencia fallida y ciclo; pruebas de arquitectura,
permutación y límites; cero WPF/Application/hardware/store concreto.

## Fase 2 — evaluator RF y dominios básicos

Implementar evaluator reentrante y detectores para `RawSigned`, `Positive`, `NegativeMagnitude` y `Absolute`.
Scratch acotado, transformaciones compartidas medidas, cancelación determinista y lote atómico. `Degraded`
se procesa con calidad degradada e `Invalid` no se procesa. Rectificada real y envolvente quedan fuera.

Gate: golden, exhaustivas pequeñas, metamórficas, concurrencia, ownership de scratch y benchmarks diagnósticos
para 2.048/65.535 muestras y tres topologías. Sin objetivo productivo aún.

## Fase 3 — orquestación post-commit

Introducir puerto sample-free de procesamiento distinto del visual, scheduler/cola acotados y reconciliación
por revisión contra la autoridad; el worker adquiere/libera leases del provider. Stop cierra admisión y drena
todo lo admitido. Definir saturación, cancelación excepcional y métricas. No persistir derivados.

Gate: ningún drop silencioso incluida la carrera commit→enqueue, reconciliación hasta `FinalRevision`, lease
exactamente una vez, commit RF independiente y carga/cierre/fallos cubiertos.

## Fase 4 — store derivado en RAM

Agregar puertos y un store derivado en memoria, acotado y reconstruible mientras exista RF, con revisión/
procedencia, vista activa sin revisiones híbridas, invalidación lógica y consulta parcial. El store RF no
cambia y sigue siendo autoridad.

Gate: atomicidad, retención explícita, revisión concurrente y cierre; no elegir todavía formato persistente.

## Fase 5 — reprocesado

Procesar por bloques/regiones desde RF almacenada con revisión de plan fijada, backpressure separado y progreso/
cancelación. Comparar live/reproceso bajo la misma semántica.

Gate: igualdad reproducible, memoria acotada, no cargar inspección completa y no privar Stop/cierre.

## Fase 6 — edición y overlay A-Scan

Añadir modelo de presentación neutral con Preview + Apply/Cancel, comandos versionados y adaptación WPF: dos límites,
umbral, sombreado, selección, teclado y publicación coherente de revisión. A-Scan no posee/evalúa el plan ni
usa puntos reducidos para detectar. Cursores A/B siguen separados.

Gate: accesibilidad, reentrada, Preview + Apply/Cancel, precisión basada en eje original y regresiones WPF;
undo queda fuera del primer incremento.

## Fase 7 — capas para C-Scan

Exponer amplitud, tiempo de detección, TOF, estado y calidad como capas distintas con máscara NoData y
procedencia. La asociación espacial se hace por `TraceIdentity`. Requiere previamente ADR independiente de
geometría/celdas/tiles de C-Scan.

Gate: ausencia no se representa como cero, consultas regionales acotadas y View sin procesamiento.

## Fase 8 — persistencia y gates productivos

Solo con requisitos y hardware acordados: elegir formato/contenedor, durabilidad, recuperación y retención;
ejecutar matriz reproducible y fijar objetivos por configuración. Cualquier NuGet requiere justificación y
aprobación previas.

Gate: recuperación, compatibilidad/versionado, benchmark registrado en `docs/performance/` y aprobación de
targets; no combinar máximos individuales como garantía.

## Validación de esta Fase 0

- revisión del diff, archivos nuevos, enlaces relativos, UTF-8 y estado Git;
- revisión lectora de arquitectura, dominio, adquisición, procesamiento, storage, visualización, calidad y repo;
- compilación y pruebas funcionales: no aplican, porque no cambia código, proyectos ni paquetes;
- benchmarks: no ejecutados; solo se define metodología.
