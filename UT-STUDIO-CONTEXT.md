# UT-Studio — Contexto inicial del proyecto

## Propósito

Crear desde cero una aplicación de escritorio para Windows destinada a adquirir, procesar, almacenar y visualizar datos ultrasónicos (UT) para ensayos no destructivos (NDT). La aplicación se desarrollará inicialmente con WPF, pero el núcleo y los ViewModels deben quedar aislados para permitir una futura interfaz Avalonia y una posible migración a Linux.

## Tecnología y forma de trabajo

- C# y .NET 10.
- UI inicial: WPF.
- Arquitectura: MVVM.
- ViewModels: CommunityToolkit.Mvvm.
- Inyección por constructor con Microsoft.Extensions.DependencyInjection/Hosting.
- Logging y configuración mediante Microsoft.Extensions.*.
- Codex integrado en Visual Studio Code para diseño e implementación.
- Visual Studio para compilación, ejecución y depuración.
- Control de versiones mediante Git.
- Ramas cortas por funcionalidad desde `main`.
- Conventional Commits.
- No hacer commit, push, merge, rebase ni tags sin petición expresa.

## Reglas arquitectónicas esenciales

1. Los ViewModels nunca se referencian entre sí directamente.
2. Los ViewModels dependen de interfaces mediante inyección por constructor.
3. El hardware no se accede desde ViewModels.
4. Los casos de uso coordinan dispositivos, sesión e inspección.
5. Los estados compartidos se exponen mediante stores/snapshots observables.
6. El Messenger se reserva para eventos ocasionales de presentación.
7. Los datos UT de alta frecuencia se transportan mediante `Channel<T>` acotados.
8. Los servicios hardware no conocen Dispatcher, ventanas ni controles.
9. Solo los proyectos terminados en `.Wpf` pueden referenciar tipos WPF.
10. Dominio, contratos, aplicación, adquisición, procesamiento y almacenamiento deben compilar sin WPF ni Avalonia.
11. Las APIs públicas del núcleo no pueden exponer `Brush`, `Color`, `Point`, `BitmapSource`, `WriteableBitmap`, `Dispatcher`, `Window` ni otros tipos de UI.
12. Toda operación larga admite `CancellationToken` y cierre ordenado.

## Comunicación de aplicación

- Servicios de aplicación para operaciones y casos de uso.
- Stores o servicios de sesión para estado compartido.
- `IObservable<T>` o mecanismo equivalente para snapshots de estado.
- `ChannelReader<T>` para frames y datos continuos.
- Messenger únicamente para notificaciones UI puntuales.
- Abstracciones portables para navegación, diálogos, selección de archivos, portapapeles y Dispatcher.

## Equipo UT y adquisición

- Equipo UT de desarrollo propio.
- Transportes: Gigabit Ethernet o PCIe.
- Existe una API para PCIe; debe encapsularse en un adaptador independiente.
- Debe existir un simulador de datos sintéticos que implemente el mismo contrato que el hardware.
- UT convencional y phased array.
- Hasta 256 canales físicos o elementos phased array.
- Hasta 65.535 muestras por A-Scan.
- Muestras de 16 bits.
- Señal RF o rectificada.
- Frecuencia máxima de muestreo: 100 MHz.
- UT convencional: hasta aproximadamente 1.000 A-Scans/s.
- Phased array: hasta 200 imágenes completas/s cuando número de muestras, leyes focales y transporte lo permitan.
- Cada imagen phased array/S-Scan contiene un A-Scan por cada ley focal activa.
- Los máximos son límites individuales y no una combinación garantizada.
- Debe calcularse y validarse el caudal de cada configuración.

### Fórmula de caudal phased array

`bytesPorImagen = leyesFocales * muestrasPorAScan * 2`

`bytesPorSegundo = bytesPorImagen * imágenesPorSegundo`

El modelo debe distinguir canal físico, elemento, beam y ley focal mediante identificadores diferentes.

## Pipeline de datos

Fuentes intercambiables bajo contratos comunes:

- Gigabit Ethernet.
- PCIe.
- Simulador.
- Reproducción desde archivo.

Pipeline conceptual:

`Fuente -> decodificación -> frames -> procesamiento -> almacenamiento -> modelos de visualización`

Requisitos:

- Memoria contigua y buffers reutilizables.
- `Memory<T>`, `Span<T>` y pooling donde resulte apropiado.
- Evitar arrays por cada ley focal y asignaciones por frame.
- El canal de visualización puede descartar frames antiguos.
- Almacenamiento no puede perder frames silenciosamente.
- Detección de secuencias perdidas, overrun, imágenes incompletas y paquetes fuera de orden.
- No bloquear el receptor de hardware sin conocer la política del dispositivo.

## Visualizaciones

Primera fase:

- A-Scan.
- B-Scan.
- C-Scan.
- S-Scan.
- D-Scan.
- Varias ventanas.

Futuro:

- Visualización 3D.

La visualización debe usar modelos neutrales, buffers de píxeles, coordenadas físicas y adaptadores específicos WPF. No debe representar cada muestra como un control XAML. La UI debe actualizarse a una frecuencia limitada e independiente de la frecuencia de adquisición.

## Dimensiones espaciales

- C-Scan habitual: hasta aproximadamente 1 x 1 metro.
- Preparación para inspecciones de hasta aproximadamente 3 x 10 metros, especialmente en cubas de inmersión.
- La resolución espacial y el tamaño físico son parámetros diferentes.
- C/B/D-Scan deben trabajar con coordenadas físicas, tiles, caché limitada y niveles multirresolución.

## Almacenamiento

- Inspecciones habituales de varios GB, aproximadamente 15–29 GB.
- Nunca cargar una inspección completa en memoria.
- Escritura incremental durante adquisición.
- Lectura por regiones o bloques.
- Índices espaciales/temporales.
- Recuperación de archivos incompletos.
- Versionado del formato.
- Previsualizaciones multirresolución.
- Los formatos externos prioritarios aún no están definidos.
- Diseñar importadores mediante interfaces/plugins sin inventar formatos concretos.

Los datos reales de inspección no se guardan en Git. El repositorio solo puede contener fixtures sintéticos pequeños y documentados.

## Informes

- Generación de informes PDF incluida desde la primera versión.
- Contenido y plantilla aún no definidos.
- Separar modelo neutral de informe y renderizador PDF.
- No elegir definitivamente librería ni plantilla antes de conocer requisitos como PDF/A, firmas, imágenes y formato corporativo.

## Hardware futuro

No se implementará en la primera fase, pero la arquitectura debe permitir añadir:

- PLC Panasonic mediante Mewtocol.
- Robot Universal Robots mediante la librería/SDK disponible.
- Encoders y sincronización posición-datos UT.

Los SDK y protocolos específicos deberán quedar aislados tras contratos propios.

## Estructura prevista de la solución

```text
src/
├── UTStudio.Domain/
├── UTStudio.Contracts/
├── UTStudio.Application/
├── UTStudio.Presentation/
├── UTStudio.Acquisition.Core/
├── UTStudio.Acquisition.GigE/
├── UTStudio.Acquisition.Pcie/
├── UTStudio.Acquisition.Simulator/
├── UTStudio.SignalProcessing/
├── UTStudio.Storage/
├── UTStudio.Visualization.Core/
├── UTStudio.App.Wpf/
├── UTStudio.Presentation.Wpf/
├── UTStudio.Visualization.Wpf/
├── UTStudio.Reporting/
└── UTStudio.Reporting.Pdf/

tests/
├── UTStudio.Domain.Tests/
├── UTStudio.Application.Tests/
├── UTStudio.Acquisition.Tests/
├── UTStudio.SignalProcessing.Tests/
├── UTStudio.Storage.Tests/
└── UTStudio.Architecture.Tests/
```

Esta estructura es una propuesta que el arquitecto debe revisar antes de crear todos los proyectos; no crear proyectos vacíos sin una justificación clara.

## Agentes personalizados previstos

- `solution_architect`: estructura, contratos, dependencias y ADR.
- `ut_domain_specialist`: modelo de dominio UT, canales, leyes, frames, unidades y geometría.
- `ut_acquisition`: GigE, PCIe, protocolos, buffers, caudal y backpressure.
- `ut_simulator`: señales sintéticas, fallos y pruebas de carga.
- `ut_processing`: DSP, gates, medidas y optimización.
- `ut_visualization`: A/B/C/S/D-Scan, coordenadas, tiles y modelos de renderizado.
- `wpf_mvvm_specialist`: Views WPF, ViewModels portables, navegación y multi-ventana.
- `ut_storage_formats`: formato nativo, lectura/escritura incremental e importadores.
- `pdf_reporting`: modelo de informe y generación PDF.
- `quality_reviewer`: corrección, concurrencia, memoria, rendimiento y pruebas; preferentemente solo lectura.
- `repository_reviewer`: revisión de diffs, Git, secretos y archivos accidentales; solo lectura.

## Reglas de colaboración de agentes

- El chat principal coordina el trabajo y consolida resultados.
- Los subagentes son temporales y reciben tareas acotadas.
- Análisis y revisiones independientes pueden realizarse en paralelo.
- Solo un agente puede escribir un archivo o módulo en una tarea.
- Cambios en contratos compartidos requieren revisión de `solution_architect`.
- Los agentes no modifican módulos ajenos sin autorización.
- Antes de editar: leer `AGENTS.md`, comprobar `git status` y preservar cambios existentes.
- Después de editar: compilar, ejecutar pruebas pertinentes y revisar el diff.
- No añadir paquetes NuGet sin justificarlo y solicitar aprobación.
- No realizar operaciones destructivas ni reescribir el historial Git.

## Estrategia de chats y Git

- Un chat nuevo por funcionalidad importante, no un único chat durante todo el proyecto.
- Una rama corta por funcionalidad.
- Guardar decisiones permanentes en `docs/adr`, requisitos o arquitectura; no depender del historial del chat.
- Usar Conventional Commits, por ejemplo `feat(acquisition): add GigE frame decoder`.
- `main` debe permanecer compilable y con pruebas superadas.

## Pendientes conocidos

- Estructura exacta de paquetes GigE.
- Detalles y documentación de la API PCIe.
- Resolución/paso espacial por eje.
- Número típico y máximo operativo de leyes focales.
- Configuraciones reales habituales de muestras y PRF.
- Política del hardware ante backpressure.
- Formatos externos prioritarios.
- Contenido y requisitos del informe PDF.
- Librería o estrategia final de renderizado.
- Librería de pruebas y mocking.
- Estrategia de instalación y actualización.

## Primera tarea para Codex

1. Leer este documento completo.
2. No generar todavía implementación funcional.
3. Crear `AGENTS.md`, `.codex/config.toml` y los agentes personalizados del proyecto.
4. Crear requisitos, arquitectura y ADR iniciales a partir de este documento, sin duplicar innecesariamente la información.
5. Crear `.editorconfig`, `.gitignore` y `README.md` iniciales.
6. Pedir a agentes especializados que revisen la propuesta en paralelo, manteniendo las escrituras bajo un único responsable.
7. No hacer commit.
8. Presentar el árbol creado, decisiones tomadas, pruebas realizadas y preguntas pendientes.
