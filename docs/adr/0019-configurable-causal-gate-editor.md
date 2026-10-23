# ADR 0019 — Editor configurable de detectores y referencias causales

Fecha: 2026-10-22. Estado: implementados la corrección de usabilidad y el bootstrap estacionario A/B/C;
la edición de detector/referencia, algoritmo v3 y eventos exactos visibles siguen aceptados pero no implementados.

## Contexto

ADR 0018 limita Preview a inicio, fin/duración y umbral de puertas existentes. Los contratos neutrales,
el compilador y el evaluator ya representan detector, dominio y referencias a eventos de otras puertas, pero
Presentation no permite editarlos. El perfil productivo actual contiene una sola puerta.

## Decisión aceptada y estado de implementación

Esta decisión complementa ADR 0018 y sustituye desde su aceptación únicamente su restricción de
campos editables. Se conservan sin cambios la autoridad única, draft no autoritativo, Preview, Apply explícito,
epochs, control de revisiones, histórico y artefacto exacto autenticado.

La aceptación no significa disponibilidad general. Esta rama implementa descubrimiento y edición de ventanas,
además del selector previo a Start y el bootstrap estacionario A/B/C descrito abajo. Detector y referencia
configurables, algoritmo v3 y eventos detectados visibles siguen sin implementar.

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

El perfil móvil actual, incluido su desplazamiento y wrap, se conserva como predeterminado. Se implementa un
perfil adicional seleccionable de ecos estacionarios con bootstrap A/B/C para inspecciones nuevas. La selección
se valida y queda congelada en el primer Start; no reemplaza planes ya configurados. El perfil concreto y sus
condiciones están definidos en
`docs/architecture/ut-gate-editor-extension-design.md`.

La selección se hace antes de **Start** mediante una opción tipada y explícita; `moving-echoes` sigue siendo el
valor predeterminado y la elección queda inmutable para toda la inspección, incluidos Stop/reinicio. La captura
reproducible distingue `SignalProfileId/Version` de `GateBootstrapProfileId/Version` y conserva eje y seed.
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
