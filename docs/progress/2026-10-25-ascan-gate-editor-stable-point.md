# Punto estable del editor de puertas A-Scan

Fecha: 2026-10-25.

## Objetivo

Consolidar y publicar un punto estable del editor de puertas A-Scan después de validar la edición con ratón,
la configuración del umbral único y el comportamiento de las puertas referidas al origen o enlazadas a otro
evento.

## Resultado

- El botón izquierdo mueve el límite de inicio, el límite final o el umbral seleccionado.
- El botón derecho traslada la ventana completa y mantiene su ancho.
- La captura de puertas usa el mismo seguimiento físico robusto que los cursores y sobrevive a publicaciones
  live compatibles y a estados visuales nulos transitorios.
- Liberar el botón derecho termina inmediatamente la captura y no deja activa la visualización de medidas ni
  obliga a realizar un primer clic izquierdo de limpieza.
- Las líneas completas de inicio, fin y umbral participan en hit testing; los círculos auxiliares de edición se
  eliminaron y se conserva únicamente el marcador del evento detectado.
- Los peers de UI Automation retirados mantienen propiedades consultables seguras y rechazan acciones mutables
  sobre identidades obsoletas, evitando excepciones de primera oportunidad durante la edición live.
- La puerta consume directamente la señal recibida y dispone de un único umbral configurable en todo su rango.
  Las transformaciones de rectificación, vídeo, Hilbert, FIR, FFT, escala logarítmica o reducción quedan para un
  futuro módulo DSP, fuera de este cambio.
- En puertas referidas al origen, el formulario muestra `Inicio` y `Fin`. En puertas causales muestra
  `Offset inicial` y `Ancho`.
- Para una puerta causal, el gráfico convierte el tiempo absoluto del puntero a offsets relativos usando el
  evento padre exacto fijado al comenzar el gesto. Sin un evento padre detectado no se ofrece captura gráfica.
- Preview y Apply siguen compartiendo el draft compilado y la geometría exacta, sin introducir una ruta de
  procesamiento alternativa.

## Decisiones confirmadas

- El componente de cálculo de puertas no transforma el dominio de señal: evalúa la señal procesada que recibe.
- El umbral es único, no simétrico y puede recorrer todo el rango de la señal de entrada.
- Las identidades de dominio legadas se conservan por compatibilidad de contratos persistentes, pero no activan
  transformaciones dentro del evaluador de puertas.
- La configuración causal se expresa como offset inicial y ancho; la proyección visual continúa siendo absoluta.
- Una captura causal requiere la detección exacta vigente del evento padre y congela ese ancla durante el gesto.

Estas decisiones están reflejadas en `docs/adr/0019-configurable-causal-gate-editor.md`,
`docs/architecture/ut-gate-editor-extension-design.md`, `docs/architecture/ut-gate-processing-design.md` y
`docs/requirements/baseline.md`.

## Archivos modificados

- Contratos, compilación, evaluación y almacenamiento de resultados de puertas.
- Sesiones y coordinador de edición en Presentation.
- Proyección, control WPF, UI Automation y vista A-Scan.
- Bootstrap del perfil estacionario y composición de la aplicación.
- Requisitos, ADR y documentación de arquitectura.
- Pruebas unitarias de núcleo, interacción WPF, composición y visualización.

El inventario exacto queda registrado por el commit asociado. La carpeta local `.tmp-video-review/` es un
artefacto temporal y queda excluida.

## Pruebas y validaciones

- Suite Core Release: 1085/1085 pruebas superadas.
- Suite WPF Release: 162/163 superadas en una ejecución completa; la única incidencia fue un timeout de 15 s en
  `StationaryProfileCreatesEditableAbcPlanAndFreezesSelectionForInspection` bajo carga.
- Repetición aislada de esa prueba estacionaria: 1/1 superada en aproximadamente un segundo.
- Pruebas de composición focalizadas: 26/26 superadas.
- Solución Release: compilación correcta, cero errores y cero advertencias.
- `git diff --check`: correcto.
- Validación manual del usuario: los últimos cambios funcionan, incluidos límites, traslado completo, umbral y
  puertas enlazadas.

## Riesgos

- La suite WPF conserva sensibilidad ocasional al límite temporal cuando se ejecuta completa; la repetición
  aislada no reproduce un defecto funcional.
- El futuro módulo DSP y la selección explícita de la representación procesada permanecen fuera de alcance.
- La creación y eliminación de puertas y la edición completa de histéresis siguen pendientes.

## Cuestiones pendientes

- Diseñar e implementar el módulo DSP que producirá las representaciones de señal consumidas por las puertas.
- Revisar en una fase posterior el timeout ocasional de la regresión WPF completa si vuelve a aparecer de forma
  reproducible.
