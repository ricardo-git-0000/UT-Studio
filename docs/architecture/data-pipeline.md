# Adquisición, caudal y memoria

Trazabilidad: R03–R07, R13; ADR 0002. Propuesta de diseño; no contrato C# ni protocolo inventado.

Fuentes GigE, PCIe, simulador y reproducción entregarán datos bajo el mismo contrato propio. El adaptador PCIe encierra el SDK y sus reglas de memoria. Separar canal físico, elemento, beam y ley focal; no inferir leyes del número de elementos.

## Presupuesto por configuración
Payload PA: bytesPorImagen = leyesFocales × muestrasPorAScan × 2.
Payload por segundo = bytesPorImagen × imágenesPorSegundo.
Convencional: sumar A-Scans/s × muestras × 2 de todos los flujos activos. Determinar si la tasa anunciada es agregada o por flujo.
100 MHz describe muestreo, no tasa sostenida de imágenes. Usar cálculos de 64 bits comprobados; incluir transporte, metadatos, procesamiento, disco y margen medido antes de admitir una configuración.

| Ejemplo hipotético | Bytes por imagen/A-Scan | MB/s | Mbit/s |
| --- | ---: | ---: | ---: |
| Convencional, 65.535 muestras, 1.000 A-Scans/s totales | 131.070 | 131,07 | 1.048,56 |
| PA, 128 leyes, 2.048 muestras, 200 imágenes/s | 524.288 | 104,8576 | 838,8608 |
| PA, 256 leyes hipotéticas, 65.535 muestras, 200 imágenes/s | 33.553.920 | 6.710,784 | 53.686,272 |

Unidades decimales: MB = 10^6 bytes. GigE nominal representa 1.000 Mbit/s antes de overhead: el primer caso ya lo excede; el segundo requiere medición y margen. El tercero NO establece 256 como límite de leyes. Ocho frames de ese tamaño retienen 268.431.360 bytes de payload, sin contar otras etapas.

## Flujo y propiedad
Fuente -> decodificación/ensamblado -> frames -> procesamiento -> persistencia y modelos visuales, con orden/bifurcación precisos pendientes de Q13.
Cada enlace usa canal acotado con presupuesto tanto de elementos como de bytes. Incluir colas, ensamblados parciales, buffers en procesamiento, leases y cachés en el límite total. Presupuestar el tiempo de absorción como bytes disponibles / exceso de bytes por segundo.
No usar dos lectores competidores de un canal para hacer broadcast: dividirían los frames. Si varias ramas necesitan el mismo dato, distribución explícita con transferencia de propiedad, leases contados o copia controlada.
Memoria contigua reutilizable; evitar arrays por ley y asignaciones por frame. El contenido publicado es inmutable. Un propietario devuelve el buffer exactamente una vez; no reutilizarlo mientras un consumidor lo conserva. Span<T> no atraviesa esperas asíncronas; Memory<T> exige vida útil documentada.
Cancelar, fallar, descartar o drenar libera todos los buffers implicados. La UI lenta no puede retener sin límite datos de adquisición.

## Saturación e integridad
Visualización puede sustituir frames antiguos, liberándolos y contabilizando descartes. Almacenamiento no descarta silenciosamente. Si el dispositivo permite pausar de forma segura, la coordinación puede usarlo; si se desconoce, no usar bloqueo del receptor como política de backpressure; emitir fallo observable y coordinar parada conforme a capacidades. Saturación/disco lleno produce estado de fallo observable, discontinuidad registrada y parada conforme a capacidades reales.
Acotar ventana y plazo de reordenación/ensamblado. Detectar pérdidas, duplicados, overrun, paquetes fuera de orden e imágenes incompletas. Decidir si estas últimas se rechazan o conservan marcadas; nunca tratarlas como completas.
Cierre normal propuesto: detener producción, completar canales, drenar persistencia, cerrar estado recuperable y liberar recursos. Definir aparte aborto y timeout sin prometer que todo lo recibido queda durable.
Métricas: bytes/frames recibidos y persistidos, pérdidas y descartes por causa, ocupación en bytes, buffers vivos, latencia y velocidad de escritura. La saturación sostenida no se resuelve aumentando indefinidamente memoria.
