# Revisión inicial — 2026-09-08

Se leyó íntegramente el contexto antes de actuar. Existían .git y UT-STUDIO-CONTEXT.md; git status inicial mostraba solo el contexto sin seguimiento. El contexto se preservó.

| Subagente temporal participante | Especialidades / áreas revisadas |
| --- | --- |
| architecture_review | solution_architect y wpf_mvvm_specialist: arquitectura y separación WPF/Avalonia |
| acquisition_review | ut_acquisition: adquisición y caudal |
| storage_quality_review | ut_storage_formats y quality_reviewer: almacenamiento y estrategia de pruebas |

Los tres trabajaron en paralelo y solo en lectura. El principal es el único escritor de todos los archivos de esta entrega. Los once perfiles creados no equivalen a once agentes ejecutados.

## Hallazgos incorporados
- Mapa lógico revisado, sin crear automáticamente 16 proyectos. Portabilidad del núcleo distinta de soporte Linux del SDK PCIe.
- Presentation portable, abstracción de hilo UI limitada a presentación y vida de ventanas separada de sesión.
- Caudal calculado por configuración: el máximo convencional de ejemplo supera GigE; 256 elementos no implica 256 leyes.
- Canales acotados también por bytes; distribución explícita, ownership y liberación de buffers incluso al descartar o cancelar.
- Saturación de persistencia observable; política de parada dependiente del dispositivo.
- Recuperación de bloques, corrupción interna y durabilidad diferenciada de simple escritura.
- Pruebas deterministas y de fallos separadas de hardware/rendimiento; librerías y umbrales todavía abiertos.

## Validación
La fase solo requiere revisión documental y de configuración. No hay solución que compilar ni pruebas funcionales que ejecutar. El informe final de la tarea distingue verificaciones ejecutadas y limitaciones del entorno. No se ha hecho commit ni staging.

Verificaciones ejecutadas: 29 archivos nuevos, 11 perfiles con campos requeridos y nombres concordantes, enlaces Markdown locales existentes, finales LF y salto final, y git diff --no-index --check sobre cada archivo nuevo (core.autocrlf=false solo para esa llamada). Sin archivos C#, soluciones o proyectos. Revisión final de los tres subagentes; incorporadas sus dos precisiones sobre cobertura de leyes focales y prohibición de bloqueo sin contrato hardware.

Limitación: se verificó el formato de agentes contra documentación oficial y se comprobaron campos/delimitadores estáticamente, pero no se validó su carga efectiva. codex doctor no pudo resolver el directorio personal/CODEX_HOME de este entorno de ejecución y no cargó configuración; --strict-config no está soportado por el subcomando features. Queda comprobar el descubrimiento de los once perfiles en una sesión local nueva de VS Code. No se modificó la configuración global ni la confianza Git: la excepción para esta ruta UNC se aplicó exclusivamente a llamadas de lectura mediante git -c safe.directory.
