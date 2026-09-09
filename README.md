# UT-Studio

Aplicación de escritorio para adquirir, procesar, almacenar y visualizar datos ultrasónicos para NDT. Objetivo tecnológico: C#/.NET 10, WPF y MVVM, con núcleo y ViewModels portables para una futura interfaz Avalonia.

Estado: fase inicial de diseño e infraestructura. No hay código funcional, proyectos C#, paquetes instalados ni instrucciones de compilación todavía. La primera versión del producto incluirá A/B/C/S/D-Scan, varias ventanas e informes PDF; esta entrega prepara ese trabajo.

## Lectura
1. [Contexto inicial](UT-STUDIO-CONTEXT.md).
2. [Reglas de colaboración](AGENTS.md).
3. [Requisitos y aceptación](docs/requirements/baseline.md) y [pendientes](docs/requirements/open-questions.md).
4. [Arquitectura](docs/architecture/overview.md), [pipeline y caudal](docs/architecture/data-pipeline.md), [almacenamiento](docs/architecture/storage.md) y [pruebas](docs/architecture/testing.md).
5. [ADR](docs/adr/README.md) y [agentes](docs/architecture/agent-infrastructure.md).
6. [Revisiones de esta fase](docs/architecture/initial-review.md).
7. [Resúmenes de progreso](docs/progress/README.md): resúmenes opcionales de fases o funcionalidades importantes.

## Trabajo posterior
Abrir un chat por funcionalidad importante, leer AGENTS.md, revisar git status y delimitar una tarea pequeña. Crear proyectos solo cuando se justifiquen por una primera funcionalidad comprobable. Codex en VS Code se utiliza para diseño e implementación; Visual Studio para compilar, ejecutar y depurar cuando exista solución.
Las configuraciones de agentes se encuentran en .codex/agents. Heredan el modelo de la sesión y se coordinan con un escritor por archivo. La activación efectiva depende del cliente y de la confianza del proyecto; véase la documentación de infraestructura.
No realizar operaciones de publicación Git sin petición expresa. Los datos reales de inspección quedan fuera del repositorio.
