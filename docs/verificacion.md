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

## Fase 3 — configuración robusta

Verificado:

- **La carga parcial funciona sobre el gateway real.** Se corrió contra un CSV
  con cinco errores deliberados (columna faltante, `DATA_TYPE` inválido, coma
  decimal en `MULTIPLICADOR`, `ACCESS_LEVEL` inválido, nombre duplicado) más tres
  tags válidos: el gateway arrancó, reportó los cinco en consola y sirvió el
  resto.
- **`ACCESS_LEVEL = Hidden` excluye el nodo del address space.** Log reportando
  10 tags válidos en cache y 9 en el árbol, y UaExpert mostrando 9. La carpeta
  intermedia del tag oculto tampoco se crea, así que no quedan carpetas vacías.
- **Un ItemID inexistente y un tag oculto se distinguen desde el cliente.** El
  primero da nodo presente con StatusCode malo; el segundo, nodo ausente. Son
  dos mecanismos separados y el árbol UA los refleja.

Tests: 39/39.

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

## Sobre el método

Las dos fallas más caras del proyecto pasaron por la suite de tests sin
inmutarse, y aparecieron corriendo el sistema completo contra algo real.

La primera fue el `SourceTimestamp` atrasado siete minutos: el driver cumplía su
contrato, así que ningún test unitario podía verlo (ver [operacion.md](operacion.md)).
La segunda fue un `MULTIPLICADOR` de `1,5` que parseaba en silencio como 15,
porque `CultureInfo.InvariantCulture` acepta la coma como separador de miles si
no se pasa `NumberStyles.Float` explícito. Los 37 tests estaban en verde: el caso
de multiplicador inválido usaba `abc`, que falla con y sin el bug. Salió al
correr el gateway contra el CSV de errores deliberados.

Las dos tienen la misma forma —el componente hace exactamente lo que promete y el
sistema igual entrega datos corruptos— y el instrumento que las encontró fue el
mismo: mirar la salida real en el borde entre dos sistemas. De ahí que el criterio
de "listo" exija verificación con los propios ojos y no solo suite en verde.