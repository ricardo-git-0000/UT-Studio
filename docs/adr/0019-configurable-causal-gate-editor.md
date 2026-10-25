# ADR 0019 — Editor configurable de detectores y referencias causales

Fecha: 2026-10-23. Estado: implementados la corrección de usabilidad, el bootstrap estacionario A/B/C, el
algoritmo v3 y la edición discriminada de detector y referencia con eventos exactos visibles.

## Contexto

ADR 0018 limita Preview a inicio, fin/duración y umbral de puertas existentes. Los contratos neutrales,
el compilador y el evaluator ya representan detector y referencias a eventos de otras puertas, pero
Presentation no permite editarlos. El perfil productivo actual contiene una sola puerta.

## Decisión aceptada y estado de implementación

Esta decisión complementa ADR 0018 y sustituye desde su aceptación únicamente su restricción de
campos editables. Se conservan sin cambios la autoridad única, draft no autoritativo, Preview, Apply explícito,
epochs, control de revisiones, histórico y artefacto exacto autenticado.

La aceptación no significa disponibilidad general. Esta rama implementa descubrimiento y edición de ventanas,
el selector previo a Start, el bootstrap estacionario A/B/C, la semántica v3 y la configuración de detector,
parámetros locales y referencia de las puertas existentes. Los eventos exactos se proyectan desde el
evaluator sobre muestras originales.

El draft podrá cambiar, para puertas existentes, ventana, detector, parámetros compatibles y referencia causal.
La cardinalidad e identidad permanecen fijas: crear y eliminar puertas queda fuera de alcance.

- El umbral es un parámetro de aceptación configurable de todos los detectores ofrecidos, incluidos
  `GlobalMaximum` y `GlobalMinimum`. Cambiar de detector conserva el umbral; una definición antigua sin umbral
  se inicializa al 50 % al cambiar de detector. Los perfiles productivos nuevos lo inicializan al 20 %.
- Los cruces conservan interpolación lineal, igualdad en la muestra alcanzada y `SampleIndex` izquierdo. La UI
  los denomina «Primer cruce ascendente/descendente» y muestra el tiempo interpolado.
- En máximos locales, `amplitud > umbral` es condición estricta de elegibilidad. El orden será: vecindad y
  meseta; prominencia; umbral estricto; ordenación y supresión por distancia solo entre elegibles; selección
  temporal first/last. Un candidato bajo umbral no puede suprimir otro por distancia.
- El evaluador consume las muestras de entrada sin transformarlas. La puerta no elige rectificación ni dominio.
  El selector de dominio desaparece y el umbral produce una única línea. `Positive`, `NegativeMagnitude`,
  `Absolute` y `AbsoluteMaximum` permanecen únicamente como identidades legadas de contratos persistentes y no
  activan transformaciones. Los planes nuevos usan la señal procesada de entrada y `GlobalMaximum/Minimum`.
- Una referencia nueva conserva el offset numérico y cambia explícitamente su origen. Self-reference, referencias
  inexistentes y ciclos se rechazan al compilar Preview. Un padre sin evento produce `DependencyNotDetected`,
  sin cero ni evento antiguo.
- Las puertas causales se configuran como `Offset inicial` y `Ancho`, aunque su geometría se proyecta en tiempo
  absoluto. La captura gráfica congela el evento padre exacto del comienzo del gesto y convierte cada límite a
  offset relativo; el botón derecho traslada la ventana por delta y conserva el ancho. Sin evento padre detectado
  no se inicia una captura gráfica de la puerta enlazada.
- Una definición antigua sin threshold lo inicializa al `50 %` al cambiar de detector; un pico inicializa además
  radio `1`, prominencia `0 counts`, distancia `1` y meseta `LowerCenter`. Los valores se muestran inmediatamente.
- Un candidato se compila antes de sustituir el draft vigente. Un rechazo conserva draft y Preview estables y
  publica sus diagnósticos como intento rechazado independiente. Apply solo admite el mismo artefacto compilado.
- La histéresis existente se conserva sin reinterpretación. El formulario la presenta como configuración de solo
  lectura y bloquea mutaciones estructurales que no pueda representar; editar la ventana no la elimina.

El cambio de orden de filtrado de máximos locales modifica resultados reproducibles y se implementa como
`GateAlgorithmVersion=3`, con firma y golden propios. El compilador actual solo admite v3: un plan v2 conservado
mantiene su identidad histórica, pero debe procesarse con un runtime v2 compatible o migrarse explícitamente a un
plan v3 nuevo. Nunca se evalúa con reglas v3 ni se sobrescribe silenciosamente su versión.

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

Los eventos se incorporan a snapshots neutrales desde resultados evaluados sobre muestras originales. La vista
no deduce tiempos ni amplitudes de puntos A-Scan reducidos. El snapshot separa amplitud usada por el detector de
la ordenada RF firmada mostrada. En cruces, esta ordenada se interpola linealmente entre las dos muestras
originales con la misma fracción del tiempo; `SampleIndex` conserva la muestra izquierda.
El resultado neutral retiene este payload exacto con independencia de las medidas pedidas por la puerta; su uso
visual exige conjuntamente identidad de fuente geométrica, traza, revisión de inspección y puerta/revisión.

## Consecuencias

Presentation necesita un formulario discriminado por detector y un selector causal. Cambiar detector o referencia
invalida captura y Preview estable incompatible. El compilador continúa siendo la única validación semántica del
plan y Apply sigue siendo la única entrada al procesamiento productivo.

## Fuera de alcance

Creación/eliminación, el módulo DSP y sus operaciones (rectificación, vídeo, Hilbert, FIR, FFT, log y reducción),
e interpolación distinta de cruces.
