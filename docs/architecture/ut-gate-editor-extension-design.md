# Ampliación del editor A-Scan: detectores, causalidad y tres puertas

Fecha: 2026-10-23. Estado: implementados usabilidad, algoritmo v3, perfil estacionario, edición discriminada y
representación exacta de eventos; creación/eliminación y el futuro módulo DSP permanecen fuera de alcance.

## Separación del trabajo

1. **Usabilidad implementada en esta rama:** descubrir y seleccionar la puerta existente; handles interiores sin alterar tiempos;
   arrastre, teclado y controles numéricos por el mismo draft; Preview y Apply separados.
2. **Semántica v3 implementada:** cruces interpolados sin cambios; máximos locales con umbral estricto antes de
   supresión por distancia y selección temporal.
3. **Edición implementada:** formulario discriminado y recompilación transaccional del DAG mediante Preview.
4. **Perfil estacionario implementado:** selector previo a Start y bootstrap A/B/C para inspecciones nuevas;
   el perfil móvil vigente se conserva como predeterminado.
5. **Eventos exactos implementados:** resultados originales proyectados con identidad completa.

Los cinco bloques están implementados para puertas existentes.

## Política editorial implementada

La mutación construye y compila un candidato completo antes de instalarlo. Draft, candidato compilado,
geometría, Preview estable y Apply comparten el mismo artefacto. Un candidato inválido conserva el último draft
válido y expone diagnósticos separados; texto inválido o mutación pendiente bloquean Apply.

Máximo y mínimo conservan threshold como criterio de aceptación y eliminan histéresis y parámetros locales.
Los cruces simples usan threshold y eliminan parámetros locales. Los picos usan threshold y parámetros locales.
Una definición antigua sin threshold se inicializa al `50 %` al cambiar de detector; al entrar en un pico se
inicializan además radio `1`, prominencia `0 counts`, distancia `1` y `LowerCenter`. Cambiar referencia conserva el offset.
Self-reference, dependencias inexistentes y ciclos son rechazados por el compilador antes de sustituir el draft.

Las configuraciones existentes con histéresis se muestran sin reinterpretación. El formulario bloquea las
mutaciones estructurales que no representa y explica la causa; las ediciones de ventana conservan la histéresis.

`NoEvent`, `BelowThreshold`, `DependencyNotDetected` y `WindowOutOfRange` son resultados geométricos tipados de
un plan válido y no impiden Preview ni Apply. Offset/referencia configurados se muestran separados de la ventana
absoluta resuelta.

## Evento exacto

Cada item de geometría copia estado, clase de evento, índice, posición fraccional, tiempo, amplitud de la señal de
entrada y métricas del evaluator. En la adaptación RF actual conserva además la ordenada firmada para presentar
el evento. Para eventos discretos usa la muestra recibida; para cruces interpola entre las dos muestras con la
misma fracción temporal y mantiene `i` como `SampleIndex`. `AScanSnapshot.Points` nunca participa en el cálculo.

## Umbral de la señal de entrada

La puerta no contiene una selección de dominio ni transforma muestras. Para `u` normalizado en `[0,1]`, el valor
se obtiene por interpolación lineal en el rango declarado por la señal de entrada. En la adaptación RF firmada
actual (`Int16`) es `-32768 + 65535u`; por ello el formulario muestra un valor firmado y el gráfico proyecta una
sola línea. Una futura salida rectificada o vídeo declarará un rango no negativo y reutilizará el mismo contrato.

`Positive`, `NegativeMagnitude`, `Absolute` y `AbsoluteMaximum` se conservan temporalmente solo para leer
identidades históricas; no provocan rectificación en el evaluator ni aparecen en el editor. Los planes nuevos
usan `GlobalMaximum/Minimum` sobre la salida ya procesada. Histéresis sigue siendo exclusiva de cruces y no puede
coexistir con threshold simple.

## Orden de máximos locales

1. Formar mesetas y exigir vecindad completa; los bordes no son candidatos.
2. Representar la meseta por su centro inferior.
3. Calcular y filtrar prominencia mínima (`>=`).
4. Filtrar amplitud con comparación estricta `> threshold`.
5. Ordenar elegibles por amplitud descendente, prominencia descendente y posición ascendente.
6. Aplicar distancia mínima únicamente entre elegibles.
7. Elegir menor posición para First o mayor para Last.

Si no existe candidato tras vecindad/prominencia, el resultado es `NoEvent`. Si existían candidatos locales
prominentes pero ninguno cumple `amplitud > threshold`, es `BelowThreshold`. Nunca se transforma el modo en cruce
ni máximo global.

Esta semántica pertenece exclusivamente a `GateAlgorithmVersion=3`. El compilador rechaza v2 con
`InvalidVersion`; los artefactos y resultados v2 conservan su versión y firma históricas y no se reinterpretan.

## Perfil estacionario A/B/C implementado

El simulador normal usa 2048 muestras a 50 MHz, offset 0 y rango `[0,40.96 µs)`. Genera ecos RF de 5 MHz,
sigma 0,35 µs y amplitudes nominales 0,6 y 0,3 en 10 y 25 µs, con ruido máximo 0,01.

El perfil móvil actual desplaza ambos ecos una muestra por frame y hace wrap; se conserva sin cambios y no es un
defecto. Por ello ninguna ventana causal B
estática y positiva puede ser válida para todos los frames cuando A puede aparecer arbitrariamente cerca del fin.
El perfil siguiente se instala solo al seleccionar explícitamente `stationary-echoes` antes de Start:

| Gate | Referencia | Detector | Offset | Duración | Umbral de entrada | Parámetros locales |
| --- | --- | --- | ---: | ---: | ---: | --- |
| A | origen | `GlobalMaximum` | 8,5 µs | 3,0 µs | +20 % | — |
| B | evento A | `GlobalMaximum` | +13,5 µs | 3,0 µs | +20 % | — |
| C | origen | `GlobalMaximum` | 23,5 µs | 3,0 µs | +20 % | — |

A cubre el primer eco. Para A detectado entre 8,5 y 11,5 µs, B resuelve entre 22 y 28 µs y permanece dentro de
la traza, cubriendo el segundo eco. C ofrece una medición independiente del segundo eco y puede cambiarse a A o B
conservando su offset numérico. El 20 % conserva margen respecto del eco simulado menor, de amplitud nominal
30 %, y permite ajustar el criterio desde el formulario o arrastrando con el botón izquierdo el handle horizontal.
Un perfil posterior destinado a demostrar picos deberá declarar radio, prominencia y distancia explícitos; no se
inventan esos parámetros en el bootstrap.

El perfil se valida contra `FirstSampleOffsetSeconds`, `SampleRateHz` y `SampleCount` antes de crear la autoridad.
Los centros y amplitudes de eco pertenecen a un perfil versionado de fuente, no a la configuración genérica de
adquisición. Los planes existentes nunca se sustituyen automáticamente.

El selector ofrece `moving-echoes` y `stationary-echoes` antes del primer **Start**. La elección queda bloqueada
para toda la inspección, incluidos Stop y reinicio. `moving-echoes` es el valor predeterminado, por lo que actualizar
la aplicación no cambia el comportamiento previo.
Una combinación incompatible se rechaza antes de autoridad/epoch mediante resultado tipado con identidad/versiones
de ambos perfiles y una causa estable equivalente a `EchoMotionMayWrapCausalWindow`. El wrap durante el perfil
móvil no se recorta: una ventana resuelta fuera de traza conserva `WindowOutOfRange`, sin cero, clipping ni anchor
antiguo.

La reproducibilidad requiere dos identidades: perfil de señal (centros, amplitudes, portadora, sigma, ruido/seed y
política de movimiento/wrap) y perfil bootstrap de gates (orden, IDs, referencias, ventanas, detector,
parámetros y medidas). La run captura ambas versiones con su eje; la misma tupla y seed produce la misma señal,
plan y firma. La composición captura estas identidades y versiones, seed y eje en
`SimulationInspectionConfiguration` antes de crear autoridad o epochs.

## Flujo editorial

Seleccionar puerta → modificar definición discriminada → compilar Preview completo → proyectar ventana, umbral y
evento exacto → Enter confirma Preview → Apply explícito autentica el mismo artefacto y publica revisión/epoch.
Cambiar referencia conserva el offset mostrado y actualiza la etiqueta «respecto de origen/A/B».

El evaluator conserva un payload exacto tipado aunque la proyección de medidas solicitadas no incluya amplitud o
tiempo. La identidad del evento nunca se usa aislada: es la composición de `GateGeometrySnapshot.Source`,
`TraceIdentity`, revisión de inspección y `GateId/GateRevision`. Esto impide mezclar un evento con otra traza,
revisión, Preview o definición de puerta.

## Validación prevista

- Entrada RF ya procesada, threshold firmado y ausencia de transformaciones dentro del evaluator.
- Picos bajo umbral que no suprimen candidatos elegibles; mesetas y bordes.
- A→B→C, ausencia de padre, self-reference y ciclos.
- Detector/referencia Preview sin efecto productivo hasta Apply.
- Perfil estable dentro del rango y rechazo de perfil incompatible con la traza.
- Eventos exactos procedentes de muestras originales, nunca del A-Scan reducido.

## Traslado de una ventana existente

El botón izquierdo conserva la edición de los límites `Start` y `End`. Un arrastre con botón derecho dentro de
la ventana proyectada crea una captura semántica `Window` y modifica únicamente `StartOffsetSeconds` mediante el
delta temporal del puntero; mantiene duración, referencia causal, detector y medidas. `Window` forma
parte de la identidad de captura, pero no se publica como handle visible, elemento de tabulación ni Automation.

Para una puerta causal, la geometría se dibuja en tiempo absoluto, mientras el draft conserva
`StartOffsetSeconds` y `DurationSeconds`. Al iniciar una captura se congela el instante exacto del evento padre:
`Start` y `End` restan ese instante antes de modificar el draft, y `Window` aplica únicamente el delta del
puntero. El formulario presenta esos mismos conceptos como `Offset inicial` y `Ancho`; una puerta referida al
origen mantiene `Inicio` y `Fin`. Si el padre no tiene un evento detectado, no se ofrece captura gráfica causal.

El gesto reutiliza la coalescencia acotada de `MoveCapture`, compilación, geometría exacta, Preview y Apply. Los
límites absolutos admiten `[DomainMinimum, DomainMaximum - Duration]`; las referencias causales conservan el
offset y dejan al compilador/evaluator producir los estados tipados pertinentes, sin clipping ni anchors antiguos.
Una ausencia visual transitoria durante live conserva el gesto; cambio real de contexto, cancelación, descarga o
cierre lo invalida.
