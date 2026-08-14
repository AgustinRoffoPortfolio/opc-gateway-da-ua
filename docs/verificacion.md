# Estado de verificación

Qué se comprobó de forma directa —consola, cliente externo, herramienta de
terceros— y qué queda abierto. Se agrega una sección por fase cerrada.

## Fase 1 — esqueleto UA

Verificado:

- **El stack OPC UA funciona en 32 bits.** Servidor levantado, certificado
  generado y cliente externo conectado, todo con el proceso en x86.
- **La profundidad arbitraria del árbol funciona.** El proyecto anterior nunca
  pasó de dos niveles. Se verificó con un tag de cuatro niveles
  (`PLANTA_01.MEDICION.CAUDAL.TOTALIZADO`): las carpetas anidadas se navegan sin
  problema desde un cliente externo.
- **Los cuatro tipos de dato se publican correctamente** (`Double`, `Boolean`,
  `Int32`, `String`), verificado leyendo desde un cliente externo.

## Fase 2 — PoC vertical

Verificado:

- **Valor, calidad y timestamp llegan intactos de punta a punta.** Valor idéntico
  al último decimal entre MatrikonOPC Explorer y UaExpert, `SourceTimestamp`
  idéntico al milisegundo, y `ServerTimestamp` ~370 ms posterior. El detalle de
  la medición y la anomalía que apareció durante la verificación están en
  [operacion.md](operacion.md).

Deuda: el video de demo está grabado pero sin editar.

## Pendientes abiertos

- **La raíz del repositorio se resuelve con el archivo de solución como ancla**,
  y falla ruidosamente si no lo encuentra. Publicado como servicio (Fase 7), ese
  archivo no va a existir, así que la ubicación de la PKI tendrá que definirse de
  forma explícita. La falla ruidosa es a propósito: es preferible a que un
  servicio arranque contra una carpeta arbitraria y regenere el certificado en
  silencio. Ver decisión 6.

- **Requisitos de arranque del cliente DA.** La librería exige que
  `Bootstrap.Initialize()` se llame lo más arriba posible del arranque, antes de
  construir cualquier host, y que el proceso corra en apartment **MTA** por la
  llamada a `CoInitializeSecurity` que hace internamente. Las aplicaciones de
  consola de .NET son MTA por defecto, pero conviene verificarlo explícitamente
  antes de dar por bueno el primer arranque con COM.