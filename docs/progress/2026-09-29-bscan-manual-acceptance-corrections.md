# Correcciones B-Scan descubiertas en aceptación manual

## Objetivo

Registrar los defectos reproducidos durante la aceptación interactiva del primer incremento B-Scan y
las decisiones duraderas que no formaban parte explícita del diseño inicial.

## Resultado

La aceptación manual dejó funcional el raster live acotado, su desplazamiento circular, el cambio de
tamaño durante adquisición y la inspección del run después de Stop. Adquisición e indexado avanzan con
cada A-Scan, mientras la representación consume snapshots coherentes a una cadencia independiente.

Se corrigieron cuatro familias de defectos observables:

- ausencia de datos hasta resize, relleno azul y huecos negros entre trazas;
- crecimiento indefinido o falta de desplazamiento al llenarse la ventana;
- saturación de la UI al reducir una ventana de 1.024 trazas durante live;
- cursor o A-Scan seleccionado ausentes después de Stop hasta provocar otro render.

## Decisiones confirmadas

- La ventana B live es un ring de tamaño seleccionable y acotado. Llena, desplaza una traza por entrada;
  la adquisición no depende de la frecuencia de presentación.
- Una ventana llena usa una columna neutral por traza para todos los tamaños hasta 1.024; WPF escala
  nearest-neighbor. Los incrementales compatibles desplazan el bitmap y escriben solo la franja nueva.
- La cadencia visual máxima es 30 Hz hasta 512 trazas y 10 Hz por encima. Puede agrupar varias trazas
  adquiridas sin perder el estado autoritativo del ring.
- Cambiar tamaño durante live invalida inmediatamente el raster incompatible. Capacidad, viewport y
  petición se publican coherentemente antes de cualquier reentrada de binding/layout; hay una sola
  recuperación full y después se reanuda el incremental.
- Tras Stop, el run queda sellado y legible hasta el siguiente run o el cierre global. Zoom, pan, resize,
  selección exacta y retorno a la última señal live siguen disponibles.
- La selección se valida contra el rango residente completo, no contra el intervalo dirty de un
  incremental. El cursor es un overlay WPF independiente y aparece sin esperar otro raster.
- La carga de paletas XML pertenece a WPF: máximo 1 MiB, sin DTD ni resolución externa, RGB validado e
  interpolación definida; Core y Presentation permanecen neutrales.
- El simulador desplaza circularmente los ecos por el rango temporal para ejercitar wrap y navegación.

## Archivos modificados

La implementación afecta simulador, Visualization.Core, Presentation, Visualization.Wpf, App.Wpf y
sus pruebas. Esta consolidación actualiza `AGENTS.md`, arquitectura de agentes y pipeline, ADR 0013 y el
plan de aceptación de la Tanda 3.

## Pruebas y validaciones

Antes de esta consolidación documental quedaron correctas 363 pruebas Core y 89 pruebas WPF, además de
`dotnet format --verify-no-changes` y `git diff --check`. Las regresiones cubren protocolo full e
incremental, todos los tamaños de ventana, redimensionado productivo bajo live, selección sobre el rango
residente, cursor independiente del raster y navegación sobre el run congelado.

La comprobación humana confirmó finalmente el desplazamiento, la selección congelada y la capacidad de
respuesta después de cambiar tamaño. Estas observaciones son aceptación funcional en la VM aprobada, no
una baseline absoluta de rendimiento.

## Riesgos

La cadencia y los límites actuales mantienen acotado el trabajo visual, pero la campaña continuada de
60 minutos sigue siendo necesaria para documentar tendencia de memoria, CPU y balances. La respuesta
de UI debe volver a verificarse si cambia la política de coalescencia, el layout o el escalado raster.

## Cuestiones pendientes

Permanecen en la Tanda 3 la campaña continuada, el diagnóstico separado de 65.535 muestras, DPI,
accesibilidad, contraste y consolidación completa de evidencias. Persistencia, GPU, eje físico,
phased array y nuevas modalidades siguen fuera del primer incremento.
