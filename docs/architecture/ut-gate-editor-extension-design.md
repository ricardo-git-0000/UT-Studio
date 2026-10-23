# Ampliación del editor A-Scan: detectores, causalidad y tres puertas

Fecha: 2026-10-22. Estado: implementados usabilidad, algoritmo v3 y perfil estacionario; edición estructural y
representación exacta de eventos siguen pendientes.

## Separación del trabajo

1. **Usabilidad implementada en esta rama:** descubrir y seleccionar la puerta existente; handles interiores sin alterar tiempos;
   arrastre, teclado y controles numéricos por el mismo draft; Preview y Apply separados.
2. **Semántica v3 implementada:** cruces interpolados sin cambios; máximos locales con umbral estricto antes de
   supresión por distancia y selección temporal.
3. **Edición aceptada, no implementada:** formulario discriminado y recompilación del DAG mediante Preview.
4. **Perfil estacionario implementado:** selector previo a Start y bootstrap A/B/C para inspecciones nuevas;
   el perfil móvil vigente se conserva como predeterminado.
5. **Eventos exactos aceptados, no implementados:** resultados originales proyectados con identidad completa.

Los bloques 1, 2 y 4 están implementados. Los bloques 3 y 5 siguen fuera de este incremento.

## Umbral por dominio

Para `u` normalizado en `[0,1]`, sin redondeo ni clamp:

| Dominio | Señal evaluada | Umbral en counts |
| --- | --- | --- |
| `RawSigned` | RF firmada | `-32768 + 65535u` |
| `Positive` | `max(RF,0)` | `32767u` |
| `NegativeMagnitude` | `max(-RF,0)` | `32768u` |
| `Absolute` | `abs(RF)` promovido | `32768u` |

`AbsoluteMaximum` solo es compatible con `RawSigned`. Máximos locales y cruces aceptan los cuatro dominios.
Máximo/mínimo global de esta configuración no llevan threshold. Histéresis sigue siendo exclusiva de cruces y
no puede coexistir con threshold simple. `RectifiedInput` no se ofrece.

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

| Gate | Referencia | Detector | Dominio | Offset | Duración | Umbral | Parámetros locales |
| --- | --- | --- | --- | ---: | ---: | ---: | --- |
| A | origen | `GlobalMaximum` | `Absolute` | 8,5 µs | 3,0 µs | ninguno | — |
| B | evento A | `GlobalMaximum` | `Absolute` | +13,5 µs | 3,0 µs | ninguno | — |
| C | origen | `GlobalMaximum` | `Absolute` | 23,5 µs | 3,0 µs | ninguno | — |

A cubre el primer eco. Para A detectado entre 8,5 y 11,5 µs, B resuelve entre 22 y 28 µs y permanece dentro de
la traza, cubriendo el segundo eco. C ofrece una medición independiente del segundo eco y puede cambiarse a A o B
conservando su offset numérico. Los thresholds se dejan ausentes porque máximo/mínimo no los utilizan en la
configuración aceptada. Un perfil posterior destinado a demostrar cruces o picos deberá declarar threshold y,
para picos, radio, prominencia y distancia explícitos; no se inventan esos parámetros en el bootstrap.

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
política de movimiento/wrap) y perfil bootstrap de gates (orden, IDs, referencias, ventanas, detector, dominio,
parámetros y medidas). La run captura ambas versiones con su eje; la misma tupla y seed produce la misma señal,
plan y firma. La composición captura estas identidades y versiones, seed y eje en
`SimulationInspectionConfiguration` antes de crear autoridad o epochs.

## Flujo editorial futuro

Seleccionar puerta → modificar definición discriminada → compilar Preview completo → proyectar ventana, umbral y
evento exacto → Enter confirma Preview → Apply explícito autentica el mismo artefacto y publica revisión/epoch.
Cambiar referencia conserva el offset mostrado y actualiza la etiqueta «respecto de origen/A/B».

## Validación prevista

- RF y los cuatro dominios; igualdad y signo de thresholds.
- Picos bajo umbral que no suprimen candidatos elegibles; mesetas y bordes.
- A→B→C, ausencia de padre, self-reference y ciclos.
- Detector/referencia Preview sin efecto productivo hasta Apply.
- Perfil estable dentro del rango y rechazo de perfil incompatible con la traza.
- Eventos exactos procedentes de muestras originales, nunca del A-Scan reducido.
