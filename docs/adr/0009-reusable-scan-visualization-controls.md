# ADR 0009 — Controles reutilizables de visualización de scans

Fecha: 2026-09-17. Estado: Aceptado e implementado para A-Scan WPF.
Trazabilidad: R01, R02, R08 y R13 en [requisitos](../requirements/baseline.md); amplía [ADR 0005](0005-initial-solution-structure.md) sin cambiar [ADR 0007](0007-frame-source-ownership.md) ni [ADR 0008](0008-latest-only-visual-delivery.md).

## Contexto

El primer incremento alojó `AScanControl` dentro de `UTStudio.App.Wpf` para cerrar la sección vertical. El control ya representa una responsabilidad reutilizable: dibuja snapshots neutrales A-Scan mediante WPF, pero no pertenece a una ventana, al composition root ni al ciclo de vida de la aplicación.

## Decisión

- `UTStudio.Visualization.Core` conserva modelos, proyección min/max y snapshots neutrales e inmutables.
- `UTStudio.Presentation` conserva estado observable, comandos y ViewModels sin referencias WPF.
- `UTStudio.Visualization.Wpf` contiene el renderizador WPF reutilizable, la transformación a coordenadas WPF, DependencyProperties y auxiliares exclusivamente gráficos. Referencia únicamente `UTStudio.Visualization.Core`.
- `UTStudio.App.Wpf` conserva ventanas, composición DI, Hosting, servicios de plataforma y ciclo de vida. Aloja el control desde XAML y no contiene su implementación gráfica.
- Un futuro `UTStudio.Visualization.Avalonia` implementará un renderizador propio sobre los mismos snapshots neutrales, sin reutilizar tipos WPF.

Los controles específicos de WPF no pretenden ser portables. Cada tipo de scan tendrá su propio componente cuando exista su responsabilidad real; no se creará un control universal B/C/S/D/A-Scan. El ensamblado no se publicará todavía como paquete NuGet.

La API pública inicial es `AScanControl`, su constructor sin parámetros, `SnapshotProperty` y la propiedad `Snapshot` de tipo `AScanSnapshot?`. El componente no conoce ViewModels, DI, Hosting, hardware ni sesiones; no inicia ni detiene adquisición, no reduce muestras y no retiene buffers UT. Dibuja el conjunto ya proyectado con `DrawingContext` y `StreamGeometry`, sin un elemento WPF por punto.

## Consecuencias

El grafo añadido es `App.Wpf → Visualization.Wpf → Visualization.Core`; Presentation continúa apuntando solo a Visualization.Core. No existe dependencia inversa ni ciclo. `UTStudio.Tests.Wpf` referencia App.Wpf y Visualization.Wpf para probar integración y componente aislado. Una amistad de ensamblado limitada permite verificar matemáticas y reloj internos sin ampliar la API pública.

La extracción conserva señal, escala RF ±100 %, línea de cero, colores, fondo, redimensionamiento, límite de puntos ya proyectados y comportamiento DPI. No añade gates, cursores, zoom, ejes físicos, temas, otros scans ni Avalonia.

## Pendientes

- Aceptación visual prolongada en configuraciones DPI reales adicionales.
- Definir componentes B/C/S/D-Scan cuando sus modelos neutrales existan.
- Evaluar empaquetado solo cuando haya un consumidor externo real.
