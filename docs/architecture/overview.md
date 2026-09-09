# Arquitectura inicial

Estado: límites aceptados desde la especificación; distribución física de proyectos propuesta, pendiente de crear incrementalmente. Trazabilidad: R01–R03, R08–R13; ADR 0001–0004.

## Mapa lógico de dependencias
La flecha A -> B significa que A puede depender de B. Ninguna dependencia inversa se permite por comodidad.

| Componente previsto UTStudio.* | Dependencias y responsabilidad |
| --- | --- |
| Domain | Sin infraestructura ni UI; IDs, unidades, geometría y reglas UT |
| Contracts | Domain; contratos propios de fuentes, sesión, procesamiento y persistencia |
| Application | Domain, Contracts; casos de uso y coordinación de ciclo de vida |
| Acquisition.Core | Domain, Contracts; ensamblado y flujo comunes |
| Acquisition.GigE / Pcie / Simulator | Acquisition.Core, Contracts; transporte o señal sintética intercambiable |
| SignalProcessing | Domain, Contracts; DSP, gates y medidas |
| Storage | Domain, Contracts; bloques, índices y adaptador de reproducción |
| Visualization.Core | Domain, Contracts; geometría, tiles y modelos neutrales |
| Reporting | Domain, Contracts; modelo neutral de informe |
| Reporting.Pdf | Reporting; renderizador PDF tras decidir requisitos |
| Presentation | Domain, Contracts; ViewModels, estado observable y abstracciones de presentación |
| Presentation.Wpf | Presentation; adaptación a servicios WPF |
| Visualization.Wpf | Visualization.Core; recursos de renderizado WPF |
| App.Wpf | Raíz de composición; ensambla Application, adaptadores y presentación mediante DI/Hosting |

No crear los 16 proyectos automáticamente. La reproducción puede comenzar como adaptador de Storage y separarse si su responsabilidad lo justifica. No crear contratos vacíos. La lista de tests del contexto también es una propuesta futura.

## Comunicación y ciclo de vida
Los casos de uso operan mediante interfaces inyectadas. Los ViewModels no acceden al hardware ni se referencian entre sí; reciben servicios y snapshots de stores mediante IObservable<T> o equivalente. Messenger sirve solo para notificaciones ocasionales de presentación. Frames circulan por ChannelReader<T> de canales acotados.
Documentar orden, versión, hilo de publicación y desuscripción de snapshots antes de concretar contratos. Las operaciones largas admiten CancellationToken. La sesión controla inicio, parada, fallo y cierre; una ventana no es propietaria implícita del dispositivo.

## Frontera WPF/Avalonia
Presentation no importa WPF ni Avalonia. Navegación, diálogos, selector de archivos, portapapeles y planificación de trabajo UI se abstraen en presentación e implementan en Presentation.Wpf. Estas abstracciones no se inyectan en hardware.
Solo proyectos .Wpf referencian WPF. Tipos UI como Brush, Color, Point, BitmapSource, WriteableBitmap, Dispatcher y Window no aparecen en APIs públicas del núcleo. Usar unidades, coordenadas y formatos de píxel neutrales explícitos.
Cada ventana posee su estado visual y suscripciones; la sesión compartida tiene vida independiente. El cierre libera vistas/suscripciones sin detener accidentalmente otra ventana. La frecuencia visual es limitada; nunca un control XAML por muestra.
La futura UI Avalonia sustituirá adaptadores y composición. La portabilidad del núcleo no garantiza soporte Linux del SDK PCIe. Aislar también SDK de futuros PLC, robot y encoders.

## Decisiones abiertas
El diagrama conceptual del contexto no prescribe una cadena síncrona. La relación entre datos visualizados y datos confirmados en disco, la bifurcación y qué señales se conservan quedan en Q06/Q13. Véanse [pipeline](data-pipeline.md), [almacenamiento](storage.md) y [pruebas](testing.md).
