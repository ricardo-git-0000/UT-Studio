# Fase 6 — corrección de aceptación manual del editor de gates

Fecha: 2026-10-22.

## Objetivo

Corregir el recorrido productivo en el que `Editar gates` entraba en `Editing` sin dejar identificar ni
modificar la puerta inicial, manteniendo Preview y Apply como operaciones distintas.

## Resultado

- `BeginEdit` reutiliza la geometría committed exacta como base visual del nuevo draft, selecciona la primera
  puerta y enfoca su inicio. No confirma ni fabrica un Preview.
- La primera captura puede partir de geometría committed. Cada modificación sigue usando el mismo
  `GatePlanDraft`, compilador, validación y evaluación Preview que ya usaba el arrastre.
- Los targets de inicio y fin se desplazan ocho DIP hacia el interior cuando caen en un borde. El snapshot
  conserva el valor físico exacto y un offset de arrastre reversible; pulsar el target sin desplazarlo no cambia
  el tiempo.
- La vista identifica la puerta y presenta inicio y fin en microsegundos. El umbral solo aparece cuando la
  definición del detector contiene uno; la puerta productiva `GlobalMaximum` no lo inventa.
- Los campos conservan el texto local mientras tienen foco: las publicaciones live no restauran el valor
  anterior durante la escritura. Al salir del campo, el adaptador WPF envía el valor al coordinador neutral.
- Una nueva identidad de traza durante la edición vuelve a evaluar el artefacto editorial vigente; no deja el
  overlay sin handles ni obliga a recompilar el draft en cada frame.
- Enter sobre el gráfico continúa confirmando únicamente el Preview. Apply sigue siendo una acción explícita.
- La admisión de interacción está acotada a una operación activa, el último `MoveCapture` pendiente y un terminal.
  Los movimientos intermedios se coalescen; el último movimiento admitido precede a `EndCapture`. Cancel, cambio
  de contexto y cierre descartan pendientes obsoletos.
- El texto numérico no parseable permanece en su campo, muestra un error visible asociado y una live region,
  publica `AutomationProperties.ItemStatus` y bloquea Apply sin alterar el último draft válido. Corregirlo elimina
  el error y vuelve a usar el Preview productivo.
- El inset de handles solo se aplica a límites visibles —incluido el fin exclusivo de traza—. Un extremo realmente
  exterior no genera handle, hit target ni Automation peer; los controles numéricos siguen editando la puerta y
  al volver al viewport se reconstruye la posición exacta desde sus valores físicos.

## Causa confirmada

La causa no era únicamente el recorte en los bordes. Había dos defectos acumulados:

1. `GatePlanEditorSession.BeginEdit` publicaba un draft que `GateOverlaySession` todavía no reconocía como
   propio; el overlay invalidaba la geometría committed y publicaba `Stale` sin handles.
2. La captura exigía un `GatePreviewArtifact`, pero este solo se obtenía pulsando Preview. Por tanto era
   necesario generar un Preview sin cambios antes de disponer de los handles usados para cambiar el draft.

Además, el plan productivo inicial ocupa todo el intervalo configurado. Sus targets quedaban centrados en los
extremos del plot —el fin exclusivo puede proyectarse ligeramente más allá de la última muestra visible— y no
existía una zona completamente interior que preservase el tiempo físico.

## Alcance de creación y eliminación

No se añaden botones de crear o eliminar. ADR 0018 limita Preview a ventanas y umbrales de puertas existentes,
y `GatePlanEditorSession` rechaza cambios de cardinalidad, identidad, referencia, detector, dominio o medidas.
La edición estructural requiere una ampliación separada que defina identidades/revisiones, dependencias y
topología causal, eliminación de puertas referenciadas, selección y efectos de Apply/reprocesado.

## Validación automática

- La aceptación STA con composición real usa el plan creado por `ProductiveGateProcessingFactory` y comprueba:
  Editar, identidad y valores iniciales, hit testing de dos handles, ausencia de umbral, cambio de inicio/fin
  mediante el recorrido productivo de captura,
  conservación del texto bajo una publicación live, salida de foco hacia el mismo Preview, Preview explícito,
  Apply y autoridad committed con los valores nuevos.
- Las pruebas neutrales cubren edición inicial sobre geometría committed sin Preview previo, captura committed,
  cambio de transformada, rechazo de rango y transición al artefacto Preview.
- Las regresiones WPF del control cubren teclado, Enter sin Apply, hit testing, captura versionada,
  resize/DPI, eventos tardíos, retirada/restauración de Automation peers fuera de viewport y recuperación de
  validación numérica accesible. No se declara una prueba manual con lector de pantalla.

Validación final ejecutada el 2026-10-23:

- `dotnet build UTStudio.sln -c Release --no-restore`: correcto, 0 errores y 0 advertencias.
- `dotnet format UTStudio.sln --verify-no-changes --no-restore`: correcto.
- `UTStudio.Core.Tests` Release: 1067/1067 superadas.
- `UTStudio.Tests.Wpf` Release: 145/145 superadas.
- Regresiones focalizadas: 14/14 de `GateOverlaySessionTests` y 2/2 STA de validación productiva/off-viewport.
- `git diff --check`: correcto. No se inspeccionó `TestResults`.

## Repetición manual en Windows

1. Ejecutar `dotnet run --project src/UTStudio.App.Wpf/UTStudio.App.Wpf.csproj -c Release`.
2. Pulsar **Start** y esperar a que la secuencia avance.
3. Pulsar **Editar gates**. Deben aparecer la identidad `default-global-maximum`, inicio `0 µs`, fin `40,96 µs`
   para la configuración normal y dos targets dentro del gráfico; no debe aparecer Umbral.
4. Arrastrar ambos targets desde su zona interior y comprobar que cambian los valores. Repetir introduciendo
   valores numéricos válidos y saliendo del campo.
5. Hacer zoom con la rueda, pan con botón central o Mayús+arrastre y confirmar que los valores físicos no derivan
   y que los targets se reproyectan.
6. Usar Tab/Mayús+Tab y flechas para recorrer/modificar handles. Enter debe confirmar Preview sin ejecutar Apply.
7. Introducir un inicio igual o posterior al fin, o un valor fuera del rango mostrado: debe verse el rechazo y
   Apply no debe aceptar ese valor.
8. Restablecer valores válidos, pulsar **Preview** y después **Aplicar**. El estado debe converger a `Committed`.
9. Pulsar **Stop** y cerrar la aplicación; ambos deben conservar capacidad de respuesta.

## Riesgos y pendientes

- La operación real con lector de pantalla y el cambio de DPI entre monitores físicos siguen siendo validaciones
  manuales; las pruebas automatizadas cubren Automation peers y transiciones DPI sintéticas.
- Crear/eliminar puertas permanece como ampliación arquitectónica pendiente, no como funcionalidad parcial.
- Esta corrección no implementa algoritmo v3, edición de detector/referencia causal, eventos detectados visibles,
  selector de perfil, fuente estacionaria ni A/B/C. Son decisiones de ADR 0019 pendientes de implementación.
- La validación con un lector de pantalla real sigue siendo manual y no se ha ejecutado ni se declara realizada.

## Archivos modificados

- Presentation: coordinador de vista y sesión de overlay.
- Visualization Core/WPF: estado del handle y conversión de coordenada de arrastre.
- A-Scan WPF: controles numéricos, adaptador de foco/live y etiquetas accesibles.
- Pruebas Core/WPF: overlay y aceptación vertical productiva.
