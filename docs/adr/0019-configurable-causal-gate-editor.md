# ADR 0019 — Editor configurable de detectores y referencias causales

Fecha: 2026-10-22. Estado: decisiones aceptadas, todavía no implementadas salvo la corrección de usabilidad
de la puerta existente. El perfil estacionario A/B/C está aceptado de forma condicionada y no está implementado.

## Contexto

ADR 0018 limita Preview a inicio, fin/duración y umbral de puertas existentes. Los contratos neutrales,
el compilador y el evaluator ya representan detector, dominio y referencias a eventos de otras puertas, pero
Presentation no permite editarlos. El perfil productivo actual contiene una sola puerta.

## Decisión aceptada y estado de implementación

Esta decisión complementa ADR 0018 y sustituye desde su aceptación únicamente su restricción de
campos editables. Se conservan sin cambios la autoridad única, draft no autoritativo, Preview, Apply explícito,
epochs, control de revisiones, histórico y artefacto exacto autenticado.

La aceptación no significa disponibilidad. Esta rama implementa solamente descubrimiento, selección y edición
de ventana/umbral de la puerta existente. Detector y referencia configurables, algoritmo v3, perfil estacionario,
A/B/C y eventos detectados visibles siguen sin implementar.

El draft podrá cambiar, para puertas existentes, ventana, detector, parámetros compatibles y referencia causal.
La cardinalidad e identidad permanecen fijas: crear y eliminar puertas queda fuera de alcance.

- `GlobalMaximum` y `GlobalMinimum` no tendrán umbral en esta configuración. Cambiar a ellos elimina el umbral
  del draft; ocultarlo no es suficiente.
- Los cruces conservan interpolación lineal, igualdad en la muestra alcanzada y `SampleIndex` izquierdo. La UI
  los denomina «Primer cruce ascendente/descendente» y muestra el tiempo interpolado.
- En máximos locales, `amplitud > umbral` es condición estricta de elegibilidad. El orden será: vecindad y
  meseta; prominencia; umbral estricto; ordenación y supresión por distancia solo entre elegibles; selección
  temporal first/last. Un candidato bajo umbral no puede suprimir otro por distancia.
- Se conservan `RawSigned`, `Positive`, `NegativeMagnitude` y `Absolute`. `Absolute` es magnitud calculada de RF,
  no una señal rectificada adquirida. `RectifiedInput` queda fuera.
- Una referencia nueva conserva el offset numérico y cambia explícitamente su origen. Self-reference, referencias
  inexistentes y ciclos se rechazan al compilar Preview. Un padre sin evento produce `DependencyNotDetected`,
  sin cero ni evento antiguo.

El cambio de orden de filtrado de máximos locales modifica resultados reproducibles. Su implementación requiere
incrementar a `GateAlgorithmVersion=3` y actualizar firma/golden tests. Un plan v2 nunca se evalúa con reglas v3;
si una versión no está soportada se rechaza de forma tipada, sin reinterpretar ni sobrescribir su identidad.

## Perfiles de simulación y bootstrap

El perfil móvil actual, incluido su desplazamiento y wrap, se conserva sin migración. Se acepta de forma
condicionada un perfil adicional y seleccionable de ecos estacionarios con tres puertas A/B/C para configuraciones
nuevas. No se reemplazan planes ya configurados y esta selección todavía no está implementada. El perfil concreto
y sus condiciones están definidos en
`docs/architecture/ut-gate-editor-extension-design.md`.

La selección futura se hará antes de **Start** mediante una opción tipada y explícita; `móvil` seguirá siendo el
valor predeterminado y la elección quedará inmutable durante la run. La selección reproducible distinguirá
`SourceSignalProfileId/Version` de `GateBootstrapProfileId/Version` y los capturará junto con eje, seed y run.
Cambiar cualquier componente crea identidad nueva; nunca reinterpreta una run.

## Representación exacta

Los eventos se incorporarán a snapshots neutrales desde resultados evaluados sobre muestras originales. La vista
no deducirá tiempos de puntos A-Scan reducidos. Ventanas, umbral y evento indicarán si proceden de committed o
Preview y conservarán identidad de plan, puerta, revisión y traza.

## Consecuencias

Presentation necesita un formulario discriminado por detector y un selector causal. Cambiar detector o referencia
invalida captura y Preview estable incompatible. El compilador continúa siendo la única validación semántica del
plan y Apply sigue siendo la única entrada al procesamiento productivo.

## Fuera de alcance

Creación/eliminación, `RectifiedInput`, interpolación distinta de cruces y una segunda ruta de procesamiento.
