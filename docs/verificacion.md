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

## Fase 4 — resiliencia

Verificado con el gateway corriendo y UaExpert conectado, matando y levantando el
servidor DA a mano. En ningún escenario el gateway se cayó ni dejó de atender
clientes UA.

| # | Escenario | Resultado |
|---|---|---|
| 1 | Arranque sin DA disponible | reintenta cada 5 s indefinidamente |
| 2 | Caída con clientes UA conectados | detección ~2-3 s (objetivo < 10 s) |
| 3 | Reconexión automática | recuperación ~6 s (objetivo < 30 s) |
| 4 | Reinicio de ambos lados | el cliente UA se reengancha solo |
| 5 | Pérdida temporal de COM | analizado, no probado por separado (ver abajo) |

- **La degradación se ve desde el cliente, no solo en el log.** Secuencia
  `Good` → `UncertainLastUsableValue` → `Good` capturada en UaExpert, con el valor
  y el `SourceTimestamp` congelados durante toda la ventana degradada y pisados
  por la primera muestra fresca. Es el criterio duro de la fase: un cliente UA se
  entera de que el dato dejó de refrescarse mirando solo el cliente.
- **Simular el DA caído exige matar el proceso, no cerrar la ventana.** El
  servidor DA es un componente COM out-of-process que Windows lanza a demanda; lo
  que se abre en el escritorio es el configurador. Ver [operacion.md](operacion.md).
- **Simular el DA ausente exige un ProgID inexistente.** Como COM relanza el
  servidor en cualquier intento de conexión, el escenario 1 se probó apuntando el
  gateway a `Matrikon.OPC.NoExiste.1`, que da `0x80040154`.
- **Tras el reinicio del gateway los tags no mienten.** La cache nace vacía y
  publica `WaitingForInitialData` hasta el primer dato real, en vez de mostrar el
  último valor conocido como si fuera fresco. Esto marca una asimetría deliberada:
  si se cae el DA, el cliente conserva el último valor con su timestamp congelado;
  si se cae el gateway, lo pierde. El gateway expone, no historiza.

### El escenario 5, analizado

"Pérdida temporal de COM" puede significar cuatro cosas y ninguna justifica una
prueba propia:

- El servidor muere: es el escenario 2 (`0x800706BA`).
- Un error transitorio de RPC o marshalling: distinto código, misma respuesta.
  `RunDaLoop` atrapa cualquier excepción sin mirar el tipo y siempre recrea la
  sesión, deliberadamente: COM tiene muchas formas de decir lo mismo.
- Pérdida de red DCOM: excluida por diseño, todo corre en la misma máquina.
- **El servidor se cuelga sin morir: no está cubierto.** No hay excepción,
  `ReadAll()` no vuelve y el hilo de adquisición queda bloqueado. El gateway
  seguiría sirviendo por UA y la cache degradaría los tags, así que el cliente se
  entera; pero nunca reconectaría, porque para reconectar hay que salir de la
  llamada. La respuesta correcta es un timeout sobre las llamadas COM. Queda como
  limitación conocida.

### A escala: 8.000 tags

El escenario 2 repetido con 8.000 tags, que es donde el alta de items en cada
reconexión deja de ser gratis.

| Métrica | 10 tags | 8.000 tags |
|---|---|---|
| Arranque: CSV cargado y address space listo | < 1 s | ~1 s |
| Detección de caída del DA | ~2-3 s | ~2-3 s |
| Alta de items tras reconectar | inmediata | 8.000 en un solo ciclo |
| Crashes | 0 | 0 |

Apareció algo que con 10 tags no se veía: **el servidor DA relanzado por COM
vuelve vacío.** Los 8.000 aliases viven en el archivo de configuración del
simulador, no en el proceso, así que al matarlo los ItemIDs dejan de existir y el
alta se rechaza — legítimamente. Con 10 tags no pasaba porque eran items nativos
del simulador, que existen siempre.

Eso terminó probando lo que el reintento de items existe para resolver: el gateway
insistió cada 30 s durante cinco minutos, atendiendo clientes UA todo ese tiempo, y
en cuanto los items volvieron a existir dio de alta los 8.000 de una y publicó
`Good`, sin reiniciarse. Con el rechazo tratado como definitivo habría hecho falta
reiniciar el gateway. En planta es el mismo caso que recargar la configuración del
servidor DA: el propio Matrikon avisa que hacerlo invalida todos los items.

Tests: 46/46.

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

La Fase 4 mostró el reverso, y conviene anotarlo para no sacar la lección de más.
El síntoma —un tag que perdía su último valor al reengancharse— se descubrió
mirando UaExpert, pero acorralarlo con el cliente habría exigido cronometrar
caídas del servidor DA una y otra vez. Se hizo al revés: el escenario completo se
escribió como test unitario en segundos, y ese test descartó la cache como
culpable, que era la sospechosa principal. Recién entonces la comparación entre lo
que la cache entregaba y lo que mostraba el cliente, en el mismo instante, dejó
ver que el valor lo descartaba el stack UA por especificación.

O sea que los dos instrumentos hacen cosas distintas y ninguno reemplaza al otro:
mirar el sistema real es lo que encuentra los problemas, y los tests son lo que
los localiza rápido y sin depender de reproducir una falla a mano. La deuda que
esto dejó al descubierto era que la degradación por antigüedad no tenía ni un
test, y se saldó en la misma sesión.