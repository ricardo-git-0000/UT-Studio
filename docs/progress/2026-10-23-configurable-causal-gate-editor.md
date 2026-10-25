# Editor configurable y causal de gates A-Scan

Fecha: 2026-10-23.

## Objetivo

Completar el bloque vertical del ADR 0019 para editar detector, dominio, threshold, parámetros locales y referencia
causal de las puertas existentes A/B/C, y mostrar su evento exacto sin crear una segunda ruta de procesamiento.

## Resultado

- Mutación transaccional: el candidato se compila antes de sustituir el draft válido y el mismo artefacto alimenta
  geometría, Preview y Apply.
- Se ofrecen seis detectores, cuatro dominios, referencias a origen o puerta existente y parámetros discriminados.
- Los defaults aceptados son threshold 50 %, radio 1, prominencia 0 counts, distancia 1 y `LowerCenter`.
- Self-reference y ciclos conservan el draft/Preview anterior y muestran el diagnóstico del intento rechazado.
- La histéresis existente se muestra sin reinterpretación y bloquea mutaciones estructurales no representables.
- El evento exacto conserva tiempo/índice/fracción y separa amplitud del dominio de la ordenada RF mostrada. Los
  cruces calculan esta última mediante interpolación de muestras originales.
- `AScanView.Root` mantiene un `DataContext` local aislado: nace y se cierra en `null`, y solo `Activate` instala
  `AScanViewModel`. El `DataContext` público heredado del control permanece intacto y ya no provoca errores
  transitorios al heredar `AScanHostViewModel` durante la construcción.
- Los `HelpText` de los parámetros locales proyectan la ausencia de diagnóstico como cadena vacía, porque la
  propiedad de Automation no admite `null`; la captura STA incluye ahora el primer `Start` productivo.
- Los `ComboBoxItem` reciben un estilo implícito con alineación explícita desde su creación, tanto en los recursos
  de la aplicación como en el componente A-Scan reutilizable. El tema de WPF ya no necesita resolver un
  `ItemsControl` ancestro antes de conectar el contenedor. La regresión STA instala el listener antes de construir
  la ventana —no después— y abre y selecciona elementos en puerta, detector, dominio y referencia.
- La geometría exacta aceptada deja de desaparecer entre frames live: las publicaciones de overlay y compuesto
  se capturan atómicamente, cada evaluación dispone de oportunidad de completar y el control conserva la última
  geometría completa durante placeholders `Evaluating`/`Stale` o un compuesto nulo transitorio. Un snapshot
  completo nuevo, `NotRequested`, cambio de run o descarga de la vista siguen sustituyendo o retirando esa geometría.
- Los campos de inicio, fin y umbral envían el valor mediante un debounce acotado de 150 ms mientras conservan el
  foco; perder el foco fuerza la misma operación inmediatamente. Ambos recorridos reutilizan validación, draft y
  `PreviewHandleValueAsync`, evitando que el texto visible quede por delante de la geometría o que `Fin` no se aplique.
- Mientras existe una evaluación fijada, los frames entrantes se coalescen en una única petición pendiente
  sustituible. La llegada de un frame nuevo ya no invalida el resultado activo: cada finalización publica
  atómicamente la señal original evaluada y su geometría exacta. La visualización avanza al ritmo del evaluator
  (la regresión productiva exige al menos cinco versiones en 250 ms), sin congelarse y sin mezclar un evento con
  una RF posterior. Se conserva como máximo una evaluación activa y una pendiente.
- La retención visual nunca reutiliza el evento detectado de una traza anterior: durante `Evaluating` mantiene
  ventana, umbral y handles, pero oculta el marcador. El texto exacto también exige geometría `Current` cuya
  `TraceIdentity` coincida con la señal compuesta. Una regresión bloquea deliberadamente el evaluator, introduce
  una traza posterior y comprueba que ambos resultados completan sin inanición y que todos los pares publicados
  conservan la misma identidad de traza; la prueba estacionaria exige además un evento exacto visible en B.
- Las reevaluaciones live aceptan tanto el candidato que todavía debe promocionarse como el Preview estable ya
  promocionado. El candidato tiene prioridad mientras existe. Esto evita que el primer resultado exacto sea seguido
  inmediatamente por publicaciones `Stale` y que la UI permanezca en «Sin geometría exacta» durante un Preview.
- El estado manual de cursores se reconcilia inmediatamente contra la señal del compuesto exacto que está en
  pantalla. Mover A/B ya no espera a que termine otra evaluación de gates ni queda visualmente oculto por un
  compuesto anterior; señal y geometría conservan su identidad exacta. La composición productiva verifica el cambio
  visible de posición y las pruebas del control mantienen captura, movimiento y exclusión con pan/gates.
- Creación/eliminación, `RectifiedInput`, algoritmo v3 y perfiles de simulación permanecen sin cambios.

- Los límites de una puerta continúan editándose con botón izquierdo. El botón derecho sobre el interior traslada
  la ventana completa manteniendo duración, referencia, detector, dominio y medidas. La captura semántica
  `Window` no crea un handle visual o de Automation y reutiliza Preview/Apply y la coalescencia existente.

## Decisiones confirmadas

Las decisiones normativas se registran en ADR 0019 y en `ut-gate-editor-extension-design.md`.

## Pruebas y validaciones

- Pruebas focalizadas Core: 29 superadas.
- Prueba vertical estacionaria A/B/C: superada.
- Regresión STA de aislamiento de bindings, reapertura, cambio de contexto y modo visual-only: superada.
- Suite Core Release: 1086 superadas.
- Regresiones de visibilidad live y composición productiva estacionaria: 2 superadas.
- Suite WPF Release: 148 superadas.
- Build Release: correcto, cero warnings y cero errores.
- `dotnet format --verify-no-changes`: correcto.
- La prueba manual permanece pendiente; no se declara ejecutada.

## Riesgos y pendientes

- La edición de histéresis permanece fuera del formulario; se conserva como solo lectura.
- Creación/eliminación de puertas y `RectifiedInput` permanecen fuera de alcance.
