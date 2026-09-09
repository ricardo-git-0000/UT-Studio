# ADR 0002 — Canales acotados y saturación observable

Fecha: 2026-09-08. Estado: Aceptado: restricciones; mecanismos concretos propuestos.
Trazabilidad: R03–R07, R13 en [requisitos](../requirements/baseline.md).

## Contexto
Datos continuos exceden el ritmo UI y pueden exceder disco/transporte.

## Decisión
Canales acotados con presupuesto en bytes, memoria reutilizada y ownership explícito. UI puede descartar antiguos; persistencia informa pérdida/fallo. Validar cada configuración y no bloquear hardware sin conocer su política.

## Alternativas
Colas ilimitadas y Messenger para frames impiden presupuestar memoria; bloquear sin contrato del equipo puede provocar overrun.

## Consecuencias y pendientes
Requiere telemetría, liberación en todos los caminos y política real del dispositivo. Fan-out, leases y capacidades se concretarán antes de implementar.
