# Encapsulación reutilizable B-Scan

## Objetivo

Encapsular la experiencia B-Scan completa en un componente WPF reutilizable conforme a ADR 0016,
manteniendo `BScanControl` independiente y sin cambiar autoridad, algoritmos ni semántica de datos.

## Resultado

Se creó `UTStudio.ScanVisualization.BScan.Wpf` con `BScanView`, adaptador, selector de remuestreo y
puerto opcional de paleta. `BScanWindow` quedó como shell de apertura/cierre. El scope público de
selección pasó a denominarse neutralmente `TraceSelectionScope`, compartiendo `TraceIdentity` y
viewport temporal sin introducir ownership de sesión, store, providers o RF.

La demo `UTStudio.BScan.Wpf.Demo` consume el componente sin referenciar `UTStudio.App.Wpf`; genera
trazas sintéticas mediante contratos públicos y permite ejercitar live, selección, ReturnToLive,
interpolación, zoom, pan y cierre.

La corrección P2 posterior eliminó la amistad del componente con `UTStudio.App.Wpf` y todas las rutas
heredadas que inyectaban ViewModel/adaptador. La activación del adaptador es transaccional: cualquier
fallo revierte suscripciones, eventos, interacción y ViewModel antes de ser observado, preservando la
excepción principal y registrando fallos secundarios de cleanup por etapa.

## Decisiones confirmadas

- Composición por scan, sin clase base universal ni extracción C-Scan/Scan2D común.
- El host conserva Start/Stop, sesión, store y composición productiva.
- El componente posee solo lifecycle de presentación y toma prestados fuente, viewport y selección.

## Archivos modificados

- Nuevo ensamblado `src/UTStudio.ScanVisualization.BScan.Wpf`.
- Nuevo contexto neutral y scope de selección en `UTStudio.Presentation`.
- Integración fina en `UTStudio.App.Wpf` y demo independiente en `samples/`.
- Pruebas WPF dedicadas y actualización de solución/documentación.

## Pruebas y validaciones

La suite dirigida B-Scan/composición y las pruebas públicas del componente cubren API, modos de
remuestreo, selección, viewport, raster completo/incremental, errores, cierre, reapertura y balances.
Los resultados finales de las tres pasadas Core/WPF y build Release quedan en el informe de entrega.

## Riesgos y pendientes

- C-Scan, extracción 2D común y empaquetado NuGet permanecen fuera de alcance.
- La demo usa `Inspection.Core` únicamente detrás de contratos públicos para producir una fuente
  controlada; no usa hardware, sesión productiva ni `UTStudio.App.Wpf`.
