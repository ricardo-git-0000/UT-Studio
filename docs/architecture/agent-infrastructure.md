# Infraestructura de agentes

El principal conserva contexto, asigna archivos y consolida. Los perfiles son capacidades disponibles, no once procesos siempre activos.

| Perfil | Responsabilidad |
| --- | --- |
| solution_architect | Estructura, contratos, dependencias y ADR. |
| ut_domain_specialist | Modelo UT, identidades, unidades y geometría. |
| ut_acquisition | GigE, PCIe, buffers, caudal y backpressure. |
| ut_simulator | Señales sintéticas, fallos y carga reproducible. |
| ut_processing | DSP, gates, medidas y optimización. |
| ut_visualization | A/B/C/S/D-Scan, tiles y modelos de renderizado. |
| wpf_mvvm_specialist | Views WPF, ViewModels portables y varias ventanas. |
| ut_storage_formats | Persistencia incremental, recuperación e importadores. |
| pdf_reporting | Modelo neutral de informe y renderizador PDF. |
| quality_reviewer | Revisión de corrección, concurrencia, memoria, rendimiento y pruebas. |
| repository_reviewer | Revisión de diffs, Git, secretos y archivos accidentales. |

## Configuración
Cada archivo .codex/agents/*.toml define name, description y developer_instructions. Los modelos y esfuerzos se heredan de la sesión. Durante la fase inicial, el agente principal es el único escritor y todos los subagentes trabajan en modo de lectura. En fases posteriores, un subagente podrá escribir solamente cuando el principal le asigne explícitamente archivos o módulos exclusivos. Los dos revisores configuran sandbox_mode = "read-only" y permanecen en modo de lectura; los demás heredan los permisos de la sesión, sin que ello sustituya la asignación explícita.
.codex/config.toml habilita agentes y limita a tres subagentes simultáneos, además del principal. Los límites del entorno pueden ser más restrictivos. Una tarea puede agrupar especialidades.
Formato comprobado con la [documentación oficial de subagentes](https://learn.chatgpt.com/docs/agent-configuration/subagents) el 2026-09-08. Cliente localizado: codex-cli 0.153.0. La carga efectiva está sujeta a confianza del proyecto y permisos del cliente; estos perfiles no alteran permisos gestionados ni garantizan que una sesión ya abierta los recargue. Iniciar una sesión nueva en la raíz para utilizarlos.

## Encargo reproducible
Indicar objetivo y requisito/ADR afectado, entradas, lista exclusiva de archivos permitidos (vacía para revisión), dependencias, restricciones, criterio de aceptación y formato de respuesta. Ejemplo: pedir a ut_acquisition revisar data-pipeline.md sin editar y devolver cálculos, riesgos y pendientes.
El coordinador registra propietario antes de editar. Revisiones independientes en paralelo; cambios compartidos se consolidan secuencialmente por su propietario. Un hallazgo no transfiere propiedad. No editar un archivo ya escrito por otro agente en la misma tarea.
Contrato compartido: solicitar revisión de solution_architect antes de incorporarlo. El autor ejecuta comprobaciones pertinentes; quality_reviewer evalúa corrección y repository_reviewer revisa diff y archivos nuevos en lectura.
