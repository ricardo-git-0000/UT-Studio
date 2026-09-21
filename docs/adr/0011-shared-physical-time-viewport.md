# ADR 0011 — Viewport temporal compartido entre representaciones UT

Fecha: 2026-09-21. Estado: Aceptado e implementado inicialmente para A-Scan.
Complementa [ADR 0009](0009-reusable-scan-visualization-controls.md) y [ADR 0010](0010-ascan-manual-cursor-measurements.md).

## Contexto

El zoom temporal del A-Scan debe poder sincronizarse con futuros B-Scan sin acoplar controles, ViewModels ni orientaciones gráficas. En A-Scan el tiempo se representa horizontalmente; en B-Scan podrá ocupar otro eje. Compartir píxeles o transformaciones WPF impediría la reutilización y mezclaría responsabilidades.

## Decisión

`Visualization.Core` define `ScanTimeViewport`, un valor neutral e inmutable expresado en segundos, y operaciones puras de zoom anclado, desplazamiento, restablecimiento y reconciliación con un dominio físico nuevo. No contiene orientación, píxeles ni tipos WPF.

`Presentation` posee una instancia observable `SharedScanTimeViewport`. La composición puede compartir esa misma instancia entre ViewModels de scans relacionados. Las intenciones se aplican en el dispatcher UI, mantienen orden y publican una nueva instancia inmutable.

Cada renderizador conserva su propia transformación. `AScanControl` recibe el viewport mediante DependencyProperty, lo aplica a su eje horizontal y publica intenciones neutrales de zoom y pan. Un futuro `BScanControl` recibirá el mismo estado y lo mapeará al eje temporal que le corresponda. Ningún control conoce al otro ni existe un control universal.

La interacción A-Scan inicial es rueda centrada en el puntero, pan mediante botón central o `Shift` más botón izquierdo, captura durante el pan y restablecimiento explícito. RF permanece fija en ±100 %; este incremento no añade zoom de amplitud, profundidad, gates ni nuevas muestras.

## Consecuencias

- El sincronismo A/B se realiza por tiempo físico, no por coordenadas de pantalla.
- El viewport se limita al dominio disponible y se reconcilia al cambiar el snapshot.
- Los cursores mantienen posiciones temporales y solo se dibujan cuando caen dentro de la ventana visible.
- El zoom no recupera precisión descartada por la reducción visual existente.
- `Visualization.Wpf` continúa dependiendo únicamente de `Visualization.Core`.

## Pendientes

- Conectar el mismo `SharedScanTimeViewport` al futuro ViewModel y control B-Scan.
- Decidir por inspección si varios grupos de scans necesitan viewports independientes o enlazados.
- Añadir profundidad únicamente cuando exista velocidad de material y contrato de calibración.
