# Plan aprobado por fases — procesamiento neutral de puertas UT

Fecha: 2026-10-18. Estado: **Fase 5 implementada y validada localmente, sobre Fase 4 implementada y Fase 3 aceptada; sin composición productiva**. ADR: [0017](../adr/0017-neutral-ut-gate-processing.md).
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

Implementado: contratos neutrales inmutables y `GatePlanCompiler`. El compilador acepta entre una puerta y
el máximo configurado, usa 8 como máximo predeterminado y aplica 16 como máximo absoluto. Cubre identidades/
revisiones, parámetros tipados, validación, DAG, orden topológico determinista, dependencias por índice y firma
SHA-256 canónica y versionada. No incluye evaluator, store ni UI.

Gate: ejemplos de tres independientes, A→B→C, A→{B,C}, dependencia fallida y ciclo; pruebas de arquitectura,
permutación y límites; cero WPF/Application/hardware/store concreto.

## Fase 2 — evaluator RF y dominios básicos

Implementado evaluator reentrante y detectores para `RawSigned`, `Positive`, `NegativeMagnitude` y `Absolute`.
Scratch acotado, transformaciones compartidas medidas, cancelación determinista y lote atómico. `Degraded`
se procesa con calidad degradada e `Invalid` no se procesa. Rectificada real y envolvente quedan fuera.

Gate: golden, exhaustivas pequeñas, metamórficas, concurrencia, ownership de scratch y benchmarks diagnósticos
para 2.048/65.535 muestras y tres topologías. Sin objetivo productivo aún.

Corrección P2 de cierre: contrato de evaluación v3 con error principal/secundarios, diagnósticos y métricas;
máximos locales `O(N log N)`; leases y buckets con presupuesto por pool compartido; referencia escalar de los
nueve detectores; benchmark independiente, cadena, fan-out y señal local adversa.

## Fase 3 — orquestación post-commit

Implementado un puerto sample-free distinto del visual, un scheduler por watermarks acotados y un catálogo
autoritativo paginado de cambios por `InspectionRevision`. El worker adquiere exactamente un lease por traza
disponible, evalúa el plan fijado y entrega el batch prestado a un sink transitorio que no lo retiene. Stop
cierra admisión, sella la run y reconcilia hasta `FinalRevision` antes de cerrar las proyecciones. La capacidad
predeterminada es dos runs, ocho streams y ocho épocas de plan por run; cada página contiene como máximo 256
cambios (límite contractual 4.096). No existe cola por traza ni store derivado.

Gate: ningún drop silencioso incluida la carrera commit→enqueue, reconciliación hasta `FinalRevision`, lease
exactamente una vez, commit RF independiente y carga/cierre/fallos cubiertos.

Cierre P1/P2/P3: matriz trazable de 73 escenarios en
[aceptación de Fase 3](2026-10-17-ut-gate-processing-phase3-acceptance.md), smoke determinista y benchmarks
propios del orquestador, contabilidad lossless por run/plan y diagnóstico multirun inmutable con historial
terminal acotado.

## Fase 4 — store derivado en RAM

Implementados puertos y un store derivado en memoria, acotado y reconstruible mientras exista RF, con revisión/
procedencia, vista activa sin revisiones híbridas, invalidación lógica y consulta parcial. El store RF no
cambia y sigue siendo autoridad.

Gate: atomicidad, retención explícita, revisión concurrente y cierre; no elegir todavía formato persistente.

Decisiones cerradas: revisión por mutación observable, batch completo como commit, clave completa sin duplicar
`TraceIdentity`, manifest terminal disperso con digest SHA-256, activación linealizable, leases de vista
coherentes, páginas 1..256 con cursor ligado al lease, `KeepUntilInvalidatedOrClose`, reclamación explícita,
límites independientes por batches/bytes lógicos, saturación inmediata y cierre que espera trabajo admitido.
El drenaje publica en bloque la cobertura terminal de todos los epochs; no admite claves de plan repetidas ni
lotes que mezclen inspección, run o revisión RF final. Evidencia terminal y tombstones forman parte del presupuesto.

## Fase 5 — reprocesado

Implementar un snapshot contractual de run sellada fijado a `SourceFinalRevision`, selección canónica de
streams/rangos dispersos, enumeración paginada y reprocesado por bloques con plan fijado. El scheduler histórico
usa admisión, workers, scratch, presupuesto y métricas separados de live; cede entre bloques y Stop cancela el
backlog sin abandonar commits ya admitidos.

Publicar batches mediante el store derivado como `StoreOnly` o `ActivateOnComplete`, sin reintentos automáticos,
sin vista parcial y con manifest formado desde identidades exactas. Un segundo intento distingue batches nuevos
de `Duplicate`. Comparar live/reproceso con el mismo evaluator y la referencia escalar.

Gate: igualdad reproducible, memoria acotada, no cargar inspección completa y no privar Stop/cierre.

La API pública actual permite construir explícitamente el scheduler con snapshot provider, store derivado,
scratch histórico, opciones y `TimeProvider`; el evaluator usa ese scratch dedicado. No se añade factory
mientras no aporte semántica adicional. No hay registro o inicio automático ni comandos WPF. La admisión de
trabajos históricos queda pendiente de un caso de uso/UI autorizado en una fase posterior. Véase la
[matriz de Fase 5](../testing/gate-reprocessing-phase5-traceability.md).

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

## Validación de Fase 1

- revisión del diff, archivos nuevos, enlaces relativos, UTF-8 y estado Git;
- revisión lectora de arquitectura, dominio, adquisición, procesamiento, storage, visualización, calidad y repo;
- formato limitado a los proyectos afectados, build Release completo y pruebas dirigidas;
- suites Core repetidas y suite WPF completa para regresión;
- revisión lectora de arquitectura, dominio, procesamiento, calidad y repositorio;
- benchmarks: no ejecutados; el presupuesto final pertenece a una fase posterior.

## Validación de Fase 2

- build Release completo sin avisos ni errores;
- pruebas Processing dirigidas, suite Core completa tres veces consecutivas y suite WPF completa;
- regresiones sensibles de evaluación repetidas diez veces;
- BenchmarkDotNet `Dry` para 2.048 y 65.535 muestras, solo como diagnóstico sin baseline;
- formato de los proyectos afectados, enlaces Markdown, UTF-8, `git diff --check`, dependencias y alcance;
- revisiones lectoras de arquitectura, dominio, procesamiento, adquisición, calidad y repositorio.
