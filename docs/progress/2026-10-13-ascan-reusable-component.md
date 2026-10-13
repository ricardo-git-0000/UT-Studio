# Componente A-Scan reutilizable

## Objetivo

Encapsular la presentación completa de A-Scan en un componente WPF reutilizable sin transferirle
autoridad sobre hardware, sesión, store o RF.

## Resultado

Se añadió `UTStudio.ScanVisualization.AScan.Wpf` con `AScanView`, toolbar, estados visuales,
interacción, accesibilidad y cierre determinista. La aplicación compone este componente en
`MainWindow`; `AScanControl` continúa disponible como renderizador independiente. La muestra
`UTStudio.AScan.Wpf.Demo` demuestra consumo sin referencia a `UTStudio.App.Wpf` con datos sintéticos.

## Decisiones confirmadas

- El host conserva Start/Stop, sesión, store y cierre global.
- El componente consume snapshots y estado mediante un contexto neutral prestado.
- La View posee un único `AScanViewModel` visual y sus suscripciones, pero ningún buffer RF o lease.
- `Context` es one-shot y `CloseAsync` constituye la barrera de cleanup.
- No se introduce base universal, B-Scan, C-Scan ni paquete NuGet en esta fase.

## Archivos modificados

Se añadieron el proyecto WPF de A-Scan, su contexto y host neutral, la muestra independiente y pruebas
dirigidas. Se actualizaron la solución, composición de `MainWindow`, ADR 0016, plan y overview.

## Pruebas y validaciones

Se validan composición independiente, cursores, estado empty/live/frozen/error y recuperación,
comandos de viewport, DPI, UI Automation, reapertura, suscripciones y fronteras de dependencias. Las
suites existentes mantienen cobertura de medidas, pan, callbacks fallidos, cierre concurrente y
balances de recursos. Build y pruebas se registran en la entrega de la fase; al existir código, la
exención de compilación documental no aplica.

## Riesgos

- La API pública aún no está publicada y puede evolucionar antes de NuGet.
- El coordinador de selección conserva por compatibilidad un nombre ligado a B-Scan; su neutralización
  queda para una fase común posterior y no altera identidad ni selección en este incremento.
- `Unloaded` no equivale a disposal: el host debe esperar siempre `CloseAsync` antes de cerrar el
  Dispatcher.

## Cuestiones pendientes

Encapsulación B-Scan, extracción 2D común con un segundo consumidor real, aplicación demostrativa
multi-scan, preparación C-Scan y estrategia de empaquetado/versionado.
