# ADR 0012 — Navegación temporal compartida y reutilizable

Fecha: 2026-09-22. Estado: Aceptado e implementado para A-Scan; contrato preparado para B-Scan.
Amplía [ADR 0011](0011-shared-physical-time-viewport.md) y conserva las fronteras de
[ADR 0009](0009-reusable-scan-visualization-controls.md) y la identidad visual de
[ADR 0010](0010-ascan-manual-cursor-measurements.md).

## Contexto

El A-Scan necesita zoom temporal, pan y reset sin acoplar el modelo a WPF. El mismo intervalo físico
debe poder compartirse con un futuro B-Scan, aunque cada representación coloque el tiempo en un eje
distinto. Una aplicación consumidora también debe poder alojar `AScanControl` de forma aislada.

## Decisión

`Visualization.Core` define `ScanTimeViewport`, sus operaciones puras, la asociación
`ScanTimeViewportBinding` con run y versión, y `LinearAxisTransform`. Ninguno contiene tipos WPF ni
presupone orientación. El zoom permitido es de 1× a 4096× y la rueda aplica un factor 1,2 por notch.
La amplitud del A-Scan permanece fija en −100…+100 %.

`Presentation` contiene `SharedScanTimeViewport`, coordinador observable y cerrable cuyo alcance decide
la composición. Un grupo de scans relacionados comparte una instancia; grupos independientes usan
instancias diferentes. Cada consumidor registra su dominio temporal. El dominio común es la
intersección de los consumidores de la run activa. Una nueva run restablece la vista y retira la
anterior; versiones obsoletas se ignoran. Para rechazar callbacks tardíos sin crecimiento ilimitado, el
coordinador conserva como máximo las 64 identidades de run retiradas más recientes; el ViewModel y la
sesión siguen siendo la barrera primaria frente a runs inactivas. Quitar un consumidor puede ampliar el dominio sin alterar el
span visible salvo por el clamping necesario.

Los ViewModels exponen el viewport asociado a su snapshot local mediante `RunId` y `SnapshotVersion`.
No se referencian entre sí. Una intención procedente de A-Scan o B-Scan modifica el mismo coordinador,
por lo que la sincronización es bidireccional y física, no basada en píxeles.

`Visualization.Wpf` adapta coordenadas neutrales a DIP. `AScanControl` conserva el viewport compatible
con el snapshot realmente admitido por latest-only; un viewport de otra run o versión no sustituye al
mostrado. El control sigue siendo reutilizable mediante DependencyProperties y eventos, sin conocer
Presentation, DI, sesiones ni otros scans.

El control distingue dos modos sin escribir internamente sus DependencyProperties. En modo directo,
cuando `ViewportBinding` conserva su origen predeterminado, `TimeViewport` es la fuente viva y cada
sustitución se aplica inmediatamente. En modo coordinado, cualquier origen efectivo no predeterminado
de `ViewportBinding` tiene precedencia, incluidos valores o bindings locales, `Style.Setter` y un resultado
explícitamente nulo:
`_displayedViewport` es solo la caché del binding cuya run y versión coinciden con el snapshot mostrado;
`TimeViewport` no actúa como fallback. Esta separación evita bucles de binding y mezclas transitorias.

La rueda sin modificadores hace zoom alrededor del puntero. El pan usa botón central o Mayús más
arrastre izquierdo. Un cursor dentro de 8 DIP tiene prioridad sobre el pan izquierdo. El doble clic en
zona libre y `ResetViewportCommand` restablecen. Existen además comandos de zoom y pan para teclado.
Resize y DPI solo cambian la transformación; no el estado físico.

La composición posee y cierra el coordinador después de los ViewModels. El cierre invalida intenciones
pendientes y establece una barrera UI. Una instancia privada creada por `AScanViewModel` se cierra con
él; una instancia inyectada es prestada y la cierra su composición.

## Consecuencias

- El futuro B-Scan puede orientar el tiempo horizontal o verticalmente sin cambiar el contrato.
- No hay dependencia A-Scan ↔ B-Scan ni bucles de notificación entre ViewModels.
- Snapshot, cursores y viewport no se mezclan entre identidades visuales.
- La intersección vacía deja el grupo sin viewport hasta disponer de dominios compatibles.
- El zoom no recupera muestras descartadas por la proyección visual.

## Aplazado

Zoom de amplitud, selección de área, gestos táctiles, profundidad, velocidad de material, gates,
implementación del B-Scan y sincronización de ejes distintos del tiempo.
