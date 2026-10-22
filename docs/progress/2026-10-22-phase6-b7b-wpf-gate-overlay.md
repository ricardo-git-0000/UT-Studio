# Fase 6 B7B — overlay WPF y accesibilidad de gates

## Objetivo

Integrar el snapshot visual portable de B7A en WPF con una única entrada indivisible, render, hit testing,
captura versionada, teclado y Automation, sin activar la composición productiva del Paso 8.

## Resultado

- `AScanControl.VisualSnapshot` es la única DependencyProperty de entrada visual. El control distingue el
  snapshot recibido del admitido por throttling y solo renderiza o acepta interacción con una transformación
  compatible con el tamaño y DPI vigentes.
- Señal, cursores, viewport, gates, selección, foco, hit testing, teclado y Automation leen la misma instancia
  mostrada. Resize, DPI, pérdida de captura/foco y Unload invalidan el gesto antes de admitir coordenadas.
- BeginCapture requiere aceptación síncrona con el `GateCaptureGeneration` emitido por la máquina portable. Sin
  consumidor, ante rechazo o si WPF no puede adquirir el ratón, el gesto no queda poseído; una aceptación que
  no puede materializarse se reconcilia mediante cancelación tipada con el mismo token. La admisión vuelve a
  comprobar el snapshot recibido después del callback reentrante: solo tolera que el propio gesto publique una
  actualización compatible con su token.
- Los Preview propios conservan la captura al mantener el token completo. Un cambio externo incompatible la
  cancela; eventos tardíos transportan el nonce del gesto que los originó.
- Los peers virtuales son estables mientras conserva identidad lógica run/traza/autoridad/base/gate/revisión/
  anchor/handle. Implementan rango y selección, marshalean todo acceso por Dispatcher y quedan definitivamente
  no disponibles al retirarse.
- `AScanViewState.VisualContent` agrupa señal, cursores y viewport de una publicación coherente. El adaptador WPF
  temporal añade solo tamaño/DPI y un overlay `NotRequested`; no fabrica autoridad ni geometría.
- Una señal legítima sin `TraceIdentity` continúa visible únicamente con overlay `NotRequested` vacío. La edición
  queda deshabilitada de forma estructural.

## Decisiones

- El throttle nunca mezcla campos: sustituye la referencia compuesta completa.
- Un cambio explícito de transformación permite admitir inmediatamente un reemplazo compatible; mientras no
  exista, no se dibuja ni se interactúa con el contenido anterior a otra escala.
- Gate tiene prioridad de hit test solo si BeginCapture es aceptado. Un rechazo deja disponibles cursor,
  doble clic y pan existentes.
- Tab usa orden estable GateId y tipo de handle, pero no se consume en los extremos. Enter confirma Preview y
  nunca representa Apply; Escape solicita restauración/cancelación. Enter y Escape no se consumen cuando el
  snapshot mostrado no procede de Preview.
- La caché Automation cambia estructura únicamente cuando cambia el conjunto de identidades lógicas, no por
  cada frame o secuencia de evaluación. Los rectángulos se obtienen transformando ambas esquinas a coordenadas
  de pantalla, sin mezclar DIP con píxeles físicos.
- La navegación Tab/Shift+Tab recorre exclusivamente handles editables en orden estable GateId/tipo. Cada
  movimiento se confirma mediante el siguiente snapshot portable; en ambos extremos el gesto queda sin consumir
  para que WPF pueda sacar el foco del control.

## Frontera con Paso 8

B7B expone eventos e intenciones neutrales y permite pruebas completas mediante un consumidor controlado. Sigue
pendiente componer `GatePlanEditorSession`, `GateOverlaySession`, evaluator exacto, forwarding productivo de
contexto/intenciones, Apply, DI y lifetimes. El fallback se sustituirá por el compuesto productivo sin recompilar
planes ni crear otra autoridad.

## Archivos

- Presentation: agregado coherente en `AScanViewState` y publicación desde `AScanViewModel`.
- Visualization.Core: disponibilidad no autoritativa restringida, identidades de handle y anclas.
- Visualization.Wpf: `AScanControl` migrado y peers Automation virtuales.
- AScan.Wpf: adaptador temporal, binding/cierre migrados.
- Pruebas WPF: interacción de gates, Automation, fallback y regresiones de cursor/pan/zoom/cierre.

## Validaciones

- Focalizadas Core B7A/B7B: 60/60.
- Focalizadas STA de interacción WPF finales: 11/11, incluidas navegación multi-handle y transición DPI.
- Core completa Release: 1054/1054.
- WPF completa Release final: 139/139.
- Build Release: correcto, 0 warnings y 0 errores.
- `dotnet format --verify-no-changes` y `git diff --check`: correctos en el estado final.

La prueba DPI cambia efectivamente la raíz visual de 1 a 1,5 mediante `VisualTreeHelper.SetRootDpi`: se ejecuta
`OnDpiChanged`, se invalida la captura anterior, se rechaza la transformación 1,0 y se admite la nueva 1,5. El
host STA sin monitor físico conserva `PointToScreen` en coordenadas lógicas; por ello no puede demostrar que 12
DIP sean 18 píxeles de un monitor real. Sí comprueba después de la transición que Automation transforma ambas
esquinas y devuelve exactamente el rectángulo producido por WPF, evitando mezclar origen físico con tamaño DIP.

## Riesgos y pendientes

- Las incidencias lifecycle y el timeout B-Scan ya documentados en B7A se mantienen separados. Una ejecución
  correcta posterior no demostraría su resolución.
- La aceptación vertical productiva pertenece al Paso 8.
