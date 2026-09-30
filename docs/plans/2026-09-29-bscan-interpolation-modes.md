# Plan de implementación de modos de remuestreo B-Scan

Estado: Fases 1–3 implementadas; fase 4 no autorizada. Fuente de diseño: [ADR 0014](../adr/0014-bscan-selectable-resampling.md).

## Alcance, propiedad y reglas

El plan añade remuestreo visual seleccionable sin modificar adquisición, RF, store, identidades ni
selección exacta. El agente principal es propietario de los archivos de cada fase; revisores son solo
lectura. No se añaden paquetes. Cada fase preserva los presupuestos, latest-only, cierre y fronteras del
ADR 0013. Kriging/GP no forma parte de las fases 1–4.

## Fase 0 — Documentación y aprobación

Entregables: ADR 0014, este plan y cambios limitados en arquitectura, pipeline, requisitos e índice ADR.

Criterios: distinción remuestreo/reconstrucción inequívoca; decisiones pendientes explícitas; enlaces y
`git diff --check` correctos; ningún cambio de código/proyecto/paquete; aprobación del usuario de los
parámetros y presupuestos que condicionan implementación.

## Fase 1 — Contrato neutral, máscara, None y Nearest

Alcance previsto: modelos Scan2D en Visualization.Core; máscara/procedencia; firma estructural; servicio
cancelable; adaptación interna de B; `None` y `Nearest`, inicialmente full. Sin UI de usuario todavía.

Aceptación:

- `None` es byte a byte idéntico al comportamiento actual en full e incremental;
- Nearest usa centros, tie hacia índice menor, singletons y bordes definidos;
- valores `short.MinValue`, cero y `short.MaxValue` conservan el mapping canónico;
- gaps/NoData nunca participan como cero ni se cruzan;
- firma antigua, token cancelado, run/revisión/viewport/tamaño obsoleto no confirman;
- memoria y owners permanecen dentro de cuota y llegan a cero al cierre.

Precisión de implementación: `None` conserva full e incremental directos. `NearestNeighbor` se publica
solo como full en esta fase; cualquier base incremental se recupera mediante una full única porque el
protocolo dirty vigente no representa todavía el soporte inverso de la transformación centro-a-centro.
Intensidad, máscara y procedencia del resultado comparten un owner acotado con slices separados; la LUT
consume intensidad y máscara y no infiere validez del byte cero.

## Fase 2 — Bilinear y Bicubic

Implementar full con máscara conservadora. Bilinear y Bicubic permanecen full-only en esta fase;
incremental con halos queda aplazado. Bicubic usa Keys/Catmull–Rom `a=-0.5`, acumulación determinista,
sin renormalizar taps inválidos y clamp final `[0,255]`.

Aceptación: vectores exactos de constantes, rampas, impulsos y bordes; ausencia de overshoot en bilinear;
overshoot bicubic controlado; gaps no cerrados; una base incremental recupera mediante una única full;
cambio rápido de modo/zoom/resize produce una sola full vigente; sin starvation de Stop/cierre.

## Fase 3 — Lanczos

Implementado Lanczos separable con radio fijo aprobado `a=3`, soporte 6×6, pesos calculados sin
asignación por píxel, halo vertical 3 y halo horizontal 3 limitado al rango residente. Permanece
full-only; una petición incremental solicita una única
recuperación full y el incremental con halo queda aplazado.

Aceptación: `sinc(0)=1`, constantes conservadas, impulso/ringing documentado, normalización completa y
clamp `[0,255]` deterministas; 36 taps conservadores del mismo segmento; recuperación full única;
256×256 y 1024×1024 medidos como diagnóstico; degradación solo por cadencia latest-only, nunca por
cambio silencioso de kernel.

Resultado diagnóstico Dry en la VM VMware: Lanczos completó aislado en 256×256 y 1024×1024 y dentro
de la matriz completa de diez casos, con código cero y balances finales cero. Los intervalos observados
fueron 109,44–116,92 ms y 825,71–934,11 ms respectivamente; no son baseline y no modifican objetivos.

## Fase 4 — UI, preferencia y aceptación

Añadir selector, parámetros aprobados, estado visible, teclado/UIA, validación y errores. La transición
de opciones/viewport/resolución/revisión es atómica. Resolver y probar la política de persistencia.

Aceptación: default/fallback `None`; modo visible durante live y run detenida; edición inválida conserva
último valor; cambio genera exactamente una recuperación full; LUT se aplica después del remuestreo;
WPF no interpola BGRA; selección sigue devolviendo traza adquirida; DPI 96/120/144/192, resize, zoom,
pan, Stop y shutdown correctos; suscripciones, CTS, owners y buffers terminan a cero.

## Fase separada — Reconstrucción GP/Kriging

Solo se planificará tras aprobar requisitos físicos/NDT, ADR propio, coordenadas calibradas, modelo,
incertidumbre, UX de estimados y presupuesto. No reutiliza el enum de remuestreo como si fuese otro
kernel gráfico.

## Matriz de pruebas

| Área | Casos mínimos |
| --- | --- |
| Vectores | 1×1, 1×N, N×1, 2×2 y rejillas pequeñas con resultado exacto por modo |
| Señal | constantes, rampas, impulsos, bordes, bipolar y los tres extremos short |
| Integridad | `NoData` aislado, franjas, gaps de índices, segmentos, wrap y expulsión |
| Protocolo | full/incremental 1/8/33/100, halos, base incompatible y recuperación full |
| Concurrencia | cancelación en cada fila/bloque, cambio de modo, stale tokens, resize/zoom/pan y nueva run |
| UI | DPI, teclado/UIA, errores, estado aplicando, modo visible, Stop y shutdown |
| Exactitud | selección B→A y medidas adquiridas nunca usan una celda derivada |
| Recursos | cuotas, pico de owners/bytes, pool, ausencia de fugas y balances cero |
| Visual | fixtures sintéticos pequeños y revisados; comparación por intensidades/máscaras, no solo screenshots |
| Arquitectura | Core/Presentation sin WPF; interpoladores sin tipos B; Wpf solo referencia Core |

La aritmética esperada se prueba con tolerancia explícita solo donde haya coma flotante; identidad,
máscaras, `None`, Nearest y determinismo son exactos. No se actualizan golden files sin explicar la
diferencia semántica.

## Benchmarks y presupuesto

Ejecutar Release en hardware registrado para 256×256 y 1024×1024; full e incrementales 1/8/33/100;
cada modo con constante, ruido determinista, gaps y peor caso de máscara. Registrar warm-up, iteraciones,
p50/p95/máximo, CPU, asignaciones, bytes/owners activos y pico, cancelación y throughput. Separar
formación B, remuestreo, LUT y commit WPF.

Objetivos iniciales del ADR 0014 son hipótesis. El incremento no sale si excede 16 MiB neutrales contando
coexistencia real, si un incremental difiere del full o si Stop/cierre pierde capacidad de respuesta. Si
no alcanza 30 Hz, la aceptación permite menor cadencia efectiva latest-only con diagnóstico y último
raster válido; no permite cambiar modo o ocultar gaps.

## Migración incremental y compatibilidad futura

No renombrar inicialmente APIs B. Introducir adaptadores internos y sustituir paso a paso la formación
directa. Generalizar solo raster, máscara/procedencia, firmas, transformaciones, kernels, buffers y
métricas. Mantener formación, ejes, selección y sincronización en B. No extraer proyecto, viewport
universal o superficie pública hasta que C-Scan aporte requisitos y un segundo uso real.

## Criterios de salida transversales

- cero P1/P2; P3 resueltos o aceptados;
- build Release y suites Core/WPF sin warnings ni flakiness;
- `dotnet format --verify-no-changes`, `git diff --check` y pruebas de arquitectura correctas;
- resultados de benchmark documentados como experimento o baseline aprobada, sin confundirlos;
- memoria, cancelación, responsiveness y balances acreditados;
- documentación sincronizada y sin fixtures reales/secretos;
- no commit, stage o integración sin autorización expresa.

## Estado de decisiones

Confirmadas para Fases 1–3: Kriging/GP separado, política conservadora de taps/segmentos,
Keys `a=-0.5`, Lanczos-3 `a=3`, None compatible, Nearest explícito y composición en Visualization.Core sin proyecto
`Scan2D.Core`. Continúan pendientes la persistencia de Fase 4 y aprobación de hardware,
metodología y objetivos antes de convertir los diagnósticos de rendimiento en gates.
