# A-Scan: traspaso de la investigación del arrastre de puertas

## Objetivo

Resumir el estado de la investigación del problema observado al editar y arrastrar
puertas A-Scan, para continuar el trabajo en un chat nuevo sin perder la evidencia
obtenida ni confundir cambios validados con cambios todavía pendientes.

## Contexto del repositorio

- Rama observada: `feature/ut-gate-ascan-editor`.
- HEAD observado: `f41ebee91f51e4adf90665fd2695a01f6f4a5a1f`.
- El árbol de trabajo contiene cambios locales amplios del editor configurable de
  ADR 0019. Deben conservarse.
- No se ha hecho stage ni commit durante esta investigación.
- No se ha inspeccionado `TestResults`.

## Síntoma reproducido

Con la aplicación ejecutada bajo el depurador de VS Code, al entrar en
`Editar gates` la ejecución se detenía y la puerta no respondía al arrastre. El
depurador mostró excepciones de primera oportunidad
`System.Windows.Automation.ElementNotAvailableException` procedentes de
`GateHandleAutomationPeer`.

Se observaron dos recorridos concretos:

1. Consulta de `GetNameCore()` sobre un peer de Automation cuya geometría ya
   había sido retirada.
2. Consulta de `IRangeValueProvider.IsReadOnly` sobre ese mismo tipo de peer
   retirado.

La parada del depurador durante estas consultas explica por qué la interacción
parecía bloqueada en Debug. La aplicación no debe lanzar por consultas de
propiedades de un peer que Automation conservó mientras WPF sustituía la
geometría visual.

## Pruebas e investigación realizadas

- Se reprodujo el comportamiento con la aplicación real y con la inspección de
  excepciones activada en el depurador.
- Se comprobó que los peers de los handles se retiran y recrean cuando cambia el
  snapshot de geometría durante la edición live.
- Se verificó que clientes de UI Automation pueden conservar una referencia al
  peer retirado y consultar después nombre, estado y `RangeValue`.
- Se revisaron por separado hit testing, captura de ratón y actualización de
  snapshots. Durante la investigación se añadieron coberturas para:
  - líneas completas de inicio y fin accionables;
  - proyección de handle retrasada respecto a la geometría exacta;
  - arrastre derecho de la ventana completa;
  - snapshot live retrasado respecto a una captura aceptada;
  - conservación de una geometría coherente mientras llega su reemplazo.
- Antes del último cambio de Automation, la selección focalizada de
  `AScanGateInteractionTests` terminó con 17 pruebas superadas. Este resultado
  no valida todavía el cambio posterior de `IRangeValueProvider.IsReadOnly`.
- Se intentó reconstruir la aplicación Debug, pero una instancia detenida en el
  depurador mantenía archivos bloqueados. El intento de detener ese proceso no
  llegó a completarse. Antes de continuar hay que comprobar si sigue activo.

## Solución implementada hasta ahora

En
`src/UTStudio.Visualization.Wpf/Controls/AScanControlAutomationPeer.cs`:

- Cada `GateHandleAutomationPeer` conserva el último
  `GateHandleVisualState` conocido.
- Las actualizaciones del snapshot refrescan esa copia mientras el peer sigue
  vigente.
- Las consultas de propiedades de un peer retirado usan el estado conservado y
  devuelven una representación segura:
  - nombre y ayuda accesibles;
  - `IsReadOnly = true`;
  - `IsSelected = false`;
  - `IsEnabled = false`;
  - elemento fuera de pantalla, sin rectángulo ni punto clicable activos.
- Las acciones sobre un peer retirado siguen rechazándose mediante
  `ElementNotAvailableException`. En particular, `SetValue`, selección y foco
  no deben actuar sobre una identidad visual obsoleta.

En `tests/UTStudio.Tests.Wpf/AScanGateInteractionTests.cs` se adaptaron las
regresiones para comprobar que las propiedades siguen siendo consultables tras
retirar el peer y que una mutación obsoleta continúa siendo rechazada.

## Estado de validación

### Confirmado

- La excepción fue observada en la composición y aplicación reales bajo el
  depurador, no solo en una fixture.
- El primer recorrido por `GetNameCore()` quedó identificado.
- Después de corregir ese recorrido, el depurador reveló el segundo recorrido
  por `IRangeValueProvider.IsReadOnly`, confirmando que había que separar todas
  las consultas de propiedades de las acciones mutables.
- Las pruebas focalizadas anteriores al último ajuste de `RangeValue` pasaron
  17/17.

### Pendiente

- Ejecutar de nuevo las pruebas focalizadas después del último ajuste de
  `IRangeValueProvider` e `ISelectionItemProvider`.
- Compilar la aplicación WPF en Debug y Release una vez cerrada la instancia
  que bloquea el binario.
- Repetir en VS Code, con excepción de primera oportunidad activa:
  `Start` -> `Editar gates` -> arrastrar inicio, fin y ventana completa.
- Confirmar que no aparece ninguna nueva consulta de propiedad de Automation
  que todavía llame al acceso estricto `Current()`.
- Ejecutar la suite WPF completa y `git diff --check` cuando la regresión focal
  quede estable.
- Confirmar manualmente que el arrastre funciona durante `Running`; esta
  comprobación no debe declararse realizada todavía.

## Comandos recomendados para continuar

Sin inspeccionar `TestResults`:

```powershell
Get-Process UTStudio.App.Wpf -ErrorAction SilentlyContinue |
    Select-Object Id, Path, HasExited

dotnet test tests/UTStudio.Tests.Wpf/UTStudio.Tests.Wpf.csproj `
    -c Release --no-restore `
    --filter "FullyQualifiedName~AScanGateInteractionTests"

dotnet build src/UTStudio.App.Wpf/UTStudio.App.Wpf.csproj `
    -c Debug --no-restore

dotnet test tests/UTStudio.Tests.Wpf/UTStudio.Tests.Wpf.csproj `
    -c Release --no-restore

dotnet build UTStudio.sln -c Release --no-restore
git diff --check
```

Ejecutar las pruebas WPF secuencialmente; ejecuciones paralelas anteriores
produjeron contención en artefactos de `MarkupCompile`.

## Criterio de aceptación pendiente

1. No se lanza `ElementNotAvailableException` al consultar propiedades de un
   handle retirado durante edición live.
2. Una acción sobre un peer retirado sigue fallando de forma explícita y no
   modifica el draft.
3. Con `Start` activo, el botón izquierdo mueve solo el límite seleccionado y
   el botón derecho mueve toda la puerta manteniendo su ancho.
4. La señal A-Scan continúa actualizándose durante el gesto.
5. Preview y Apply conservan sus semánticas existentes.
6. Cierre y reactivación no dejan peers o capturas utilizables después de haber
   quedado obsoletos.

## Riesgos y cuestiones pendientes

- P1: ninguno confirmado en el estado documentado.
- P2: el último cambio de consultas `RangeValue` todavía no tiene ejecución de
  regresión posterior; no debe considerarse cerrado.
- P3: auditar todos los getters de Automation para garantizar que únicamente
  las acciones usan el acceso estricto a la geometría vigente.
- El árbol contiene otros cambios del bloque vertical ADR 0019. Una revisión o
  reversión no debe atribuirlos automáticamente a esta corrección ni
  descartarlos.

## Archivos directamente relacionados con el último problema

- `src/UTStudio.Visualization.Wpf/Controls/AScanControlAutomationPeer.cs`
- `src/UTStudio.Visualization.Wpf/Controls/AScanControl.cs`
- `src/UTStudio.ScanVisualization.AScan.Wpf/AScanView.xaml.cs`
- `tests/UTStudio.Tests.Wpf/AScanGateInteractionTests.cs`

El inventario completo del árbol modificado debe obtenerse de nuevo con
`git status --short` al iniciar el siguiente chat.

## Resolucion posterior

La continuacion identifico una segunda causa productiva independiente de UI Automation: cada frame live compatible
podia cancelar la captura portable entre `MouseDown` y el primer `MouseMove`, y el coordinador descartaba ademas
interacciones pendientes ante cualquier actualizacion visual. La captura ahora conserva su token frente a frames de
la misma run y el mismo transform durante el gesto; los cambios reales de transform, run, autoridad o ciclo de vida
siguen invalidandola. El coordinador conserva movimientos pendientes solo mientras el propietario del gesto sigue
siendo la puerta.

Se completo tambien la semantica segura de peers retirados: dejan de anunciar foco y todas sus acciones mutables,
incluido `RemoveFromSelection`, rechazan la identidad obsoleta. El inicio de arrastre de ventana aplica la misma
reconciliacion reentrante que el arrastre de un limite.

Validacion realizada el 2026-10-25:

- `GateOverlaySessionTests`: 17/17.
- `AScanGateInteractionTests`: 18/18.
- suite Core completa: 1089/1089.
- suite WPF completa: 155/155.
- aplicacion WPF Debug y solucion Release: compilacion correcta, sin avisos.
- comprobacion real con `Running`: el arrastre izquierdo cambio el inicio de `0` a `3,569 us` mientras la secuencia
  live siguio avanzando.

La automatizacion Windows disponible no permite emitir un drag con boton derecho. El traslado completo conserva su
duracion en las regresiones de sesion y WPF, pero la comprobacion visual manual del boton derecho queda pendiente.

Una repeticion posterior encontro otra cancelacion determinista: `AScanVisualSnapshotAdapter` puede publicar `null`
durante la convergencia normal de un frame live, y `AScanControl` lo trataba como contexto incompatible antes de
alcanzar su logica de retencion. Esto liberaba el raton y cancelaba tanto limites como ventana. El control conserva
ahora cualquier captura gate mientras el valor sea transitoriamente `null`; descarga, cambio real de transform o un
snapshot no nulo incompatible siguen cancelando. La regresion focal reproduce `BeginCapture -> null -> snapshot
compatible -> MoveCapture` y la suite WPF queda en 156/156.

La puerta predeterminada `default-global-maximum` ocupa todo el dominio (`0-40,96 us`). Por definicion, un arrastre
derecho que conserva su ancho no puede trasladarla hasta que se reduzca su duracion moviendo antes inicio o fin;
no es una perdida de captura. Las puertas A/B/C del perfil estacionario tienen ancho menor y recorrido disponible.

La ultima causa productiva estaba en el seguimiento fisico: el control solo continuaba por `PreProcessInput` y
`CompositionTarget.Rendering` cuando el propietario era un cursor, no cuando era una puerta. En una entrada real
o automatizada cuyos `MouseMove` enrutados se coalescen, la captura comenzaba correctamente pero no generaba el
movimiento. El seguimiento fisico cubre ahora tanto cursores como puertas y existe una regresion que reproduce esa
coalescencia.

Comprobacion final del 2026-10-25:

- con adquisicion `Running` y puerta configurada exactamente en `5-10 us`, el arrastre izquierdo cambio el inicio
  de `5` a `6,924 us` y mantuvo el final en `10 us` mientras la secuencia live continuo avanzando;
- `AScanGateInteractionTests`: 23/23;
- suite WPF completa: 160/160;
- suite Core completa: 1089/1089;
- la instrumentacion temporal usada para localizar la fase del gesto fue retirada del codigo productivo.
