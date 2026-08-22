# Traducción de calidad: OPC DA → OPC UA

> **Estado:** verificado contra el SDK y contra la especificación. Los códigos DA
> salieron de enumerar los tipos reales de la librería cliente; los `StatusCode`
> UA, de consultar el stack de la OPC Foundation. Ninguno se copió de memoria.

## Por qué hace falta traducir

Los dos estándares dicen lo mismo con vocabularios distintos e incompatibles.
Un valor no viaja solo: viaja con una afirmación sobre cuánto se puede confiar en
él. Si esa afirmación se pierde en el camino, el cliente recibe un número que
parece válido y no lo es.

**Esta es la parte conceptualmente difícil del proyecto.** El resto es plomería.

## Cómo es la calidad del lado DA

No es un booleano ni un enum plano: son 16 bits que combinan cuatro cosas.

```
 bits 15-8 │ bits 7-6 │ bits 5-2  │ bits 1-0
  Vendor   │  Quality │ Substatus │  Limit
```

- **Quality** — el nivel general: `Bad` (0), `Uncertain` (64) o `Good` (192).
- **Substatus** — la causa concreta dentro de ese nivel. No es lo mismo un `Bad`
  porque se cayó la comunicación que un `Bad` porque el instrumento está fuera de
  servicio: el primero se resuelve solo, el segundo requiere que alguien vaya.
- **Limit** — si el valor está pegado a un límite (alto, bajo o constante).
- **Vendor** — 8 bits libres para el fabricante del servidor DA.

Los valores numéricos no son arbitrarios: `Good` vale 192 porque es `11` corrido
seis lugares, y cada substatus incrementa de a 4 porque ocupa los bits 5-2. Por
eso `BadCommFailure` vale 24 y no 6.

La consecuencia práctica: aplanar la calidad DA a "sirve / no sirve" tira a la
basura la información que hace útil un diagnóstico. Es la diferencia con Modbus,
donde la calidad efectivamente se reduce a si el dispositivo contestó.

## Cómo es del lado UA

UA tiene `StatusCode`, un código de 32 bits con su propio catálogo de nombres.
Cubre aproximadamente las mismas situaciones, pero los nombres no coinciden.

Lo que no es obvio a primera vista: la OPC Foundation **reservó un rango de
`StatusCode` para espejar la calidad DA**. Los códigos `0x8089` a `0x808D` son
consecutivos y corresponden, en orden, a los mismos substatus que DA numera del 4
al 28. La traducción no es una interpretación nuestra: el estándar la previó
porque sabía que todo el mundo iba a tener que migrar.

## Fuente de la tabla

La tabla **no es propia**. Es la Tabla A.3 de la especificación OPC UA Parte 8
(Data Access), Anexo A, sección A.3.2.3 — que es **normativa**, no informativa.

El Anexo A describe dos componentes. El que nos aplica es el *COM UA Wrapper*: un
servidor OPC UA que envuelve un servidor OPC DA para que clientes UA accedan a sus
datos. Es exactamente lo que hace este gateway, así que la dirección de la tabla
es la correcta. (El otro, el *COM UA Proxy*, va al revés y su tabla A.7 no sirve
acá.)

Fuente: https://reference.opcfoundation.org/specs/OPC-10000-8/annex-a

## La tabla

Va en una **tabla explícita**, en un solo lugar, no repartida en condicionales por
el código. Es la regla de negocio central del gateway y tiene que poder leerse de
un vistazo, auditarse y modificarse sin tocar la lógica de adquisición.

La columna "DA" usa los nombres del SDK cliente; la especificación usa los mismos
conceptos con otra grafía (`LAST_USABLE`, `Uncertain_LastUsableValue`).

| Calidad DA (SDK) | Valor | StatusCode UA | Código UA |
|---|---|---|---|
| `Good` | 192 | `Good` | `0x00000000` |
| `GoodLocalOverride` | 216 | `GoodLocalOverride` | `0x00960000` |
| `Uncertain` | 64 | `Uncertain` | `0x40000000` |
| `UncertainLastUsableValue` | 68 | `UncertainLastUsableValue` | `0x40900000` |
| `UncertainSensorNotAccurate` | 80 | `UncertainSensorNotAccurate` | `0x40930000` |
| `UncertainEngineeringUnitsExceeded` | 84 | `UncertainEngineeringUnitsExceeded` | `0x40940000` |
| `UncertainSubNormal` | 88 | `UncertainSubNormal` | `0x40950000` |
| `Bad` | 0 | `Bad` | `0x80000000` |
| `BadConfigurationError` | 4 | `BadConfigurationError` | `0x80890000` |
| `BadNotConnected` | 8 | `BadNotConnected` | `0x808A0000` |
| `BadDeviceFailure` | 12 | `BadDeviceFailure` | `0x808B0000` |
| `BadSensorFailure` | 16 | `BadSensorFailure` | `0x808C0000` |
| `BadLastKnown` | 20 | `BadOutOfService` | `0x808D0000` |
| `BadCommFailure` | 24 | `BadNoCommunication` | `0x80310000` |
| `BadOutOfService` | 28 | `BadOutOfService` | `0x808D0000` |
| `BadWaitingForInitialData` | 32 | `BadWaitingForInitialData` | `0x80320000` |

Todo código DA que no esté en la tabla cae en `Bad` genérico. Eso incluye el nivel
maestro `Error` (128), que la especificación marca como reservado y no debería
aparecer nunca. "No debería pasar" no es lo mismo que "no va a pasar".

## Las dos pérdidas de información

La traducción no es reversible, y conviene saber exactamente dónde se pierde.

**1. `BadLastKnown` y `BadOutOfService` caen los dos en `BadOutOfService`.** Son
situaciones distintas en DA —"perdí contacto y te doy el último valor que supe" vs
"el item está deshabilitado"— y en UA quedan indistinguibles. Que la pérdida es
deliberada del estándar se confirma mirando la tabla inversa (A.7), que tiene una
fila menos y omite `LAST_KNOWN` por completo.

Nota de diseño: la intuición dice que `BadLastKnown` debería subir a
`UncertainLastUsableValue`, porque el dato existe aunque sea viejo y UA tiene un
código que dice exactamente eso. **La especificación decidió lo contrario** y
mantiene la severidad en `Bad`. Se sigue lo normativo: un cliente que filtre por
severidad tiene que ver lo mismo acá que en cualquier otro gateway conforme.

**2. Los 8 bits de fabricante se descartan.** La especificación lo indica
explícitamente. Un servidor DA que use ese espacio para diagnóstico propio pierde
esa información al pasar por el gateway.

## Las calidades que no vienen del DA

Todo lo anterior describe la traducción de una calidad que **llegó** del servidor
DA. Pero hay situaciones en las que no hubo lectura de la cual traducir nada, y
el gateway igual tiene que publicar algo: un nodo UA no puede quedarse sin
`StatusCode`. Esas calidades las fabrica el gateway (`TagQuality` en
`Gateway.Core`) y después pasan por el mismo `QualityMapper` que las reales.

| Situación | `TagQuality` | StatusCode UA |
|---|---|---|
| El servidor DA rechazó el ItemID al darlo de alta | `ItemRejected` | `BadConfigurationError` |
| Se pidió un tag que la cache no conoce | `UnknownTag` | `BadConfigurationError` |
| Llegó un valor pero no convierte al `DATA_TYPE` del CSV | `ConversionError` | `BadConfigurationError` |

**Las tres caen en el mismo código, y está bien que así sea de cara al cliente
UA:** la norma no ofrece nada más fino, y las tres son efectivamente errores de
configuración —el CSV declara algo que el otro lado o el dato no honran—, no
fallas de comunicación. Un cliente que las viera como `BadNotConnected` saldría a
revisar la red por un ItemID mal escrito.

**Pero es un tercer colapso, y a diferencia de los dos anteriores este no lo
impone la especificación: lo elegimos nosotros.** La consecuencia práctica es que
el `StatusCode` no alcanza para diagnosticar, porque las tres se arreglan en
lugares distintos: la columna `TAG_NAME_OPC_DA` del CSV, un tag que no debería
estar pidiéndose, y la columna `DATA_TYPE`. Por eso la distinción tiene que
sobrevivir del lado del gateway aunque se pierda del lado UA.

Hoy sobrevive parcialmente: los contadores y la heurística de diagnóstico (Fase 5)
no miran el substatus sino si el tag alguna vez respondió, que es lo que separa
"el ItemID no existe" de "el servidor dejó de contestar". Lo que todavía no
distingue es la tabla de la página de diagnóstico, que muestra el `StatusCode` y
por lo tanto exhibe los tres casos como `BadConfigurationError`. Queda anotado
como deuda: la fila debería mostrar el `TagQuality` nominal, no su traducción.

### Una colisión de vocabulario en los logs

Cuando el servidor DA rechaza un ItemID al darlo de alta, el log dice que el item
queda *"fuera de servicio"*. Es desafortunado: en el vocabulario OPC DA
`Bad_OutOfService` significa otra cosa —el item existe pero está deshabilitado— y
tiene su propio substatus, que no es el que se aplica acá. Son dos caminos
distintos con nombres que se pisan. El texto del log debería decir "rechazado".

## Qué pasa con la transformación de unidades

La transformación es `Valor_UA = Valor_DA * MULTIPLICADOR + OFFSET`.

**Solo se aplica si el valor es numérico y la calidad es buena o utilizable.** Si
la calidad es mala, o el valor no convierte al tipo declarado, el nodo publica un
`StatusCode` no-bueno y no se publica un valor escalado.

El motivo: un valor escalado sobre una lectura mala es peor que no publicar nada,
porque **parece válido**. Pasó por una fórmula, tiene la magnitud correcta y las
unidades correctas. Nada en el número delata que la lectura de la que salió no
servía.

## Timestamps

La especificación (A.3.2.4) confirma la decisión que ya estaba tomada: el
timestamp que entrega el servidor DA se asigna al **SourceTimestamp**, y el
**ServerTimestamp** lo pone el gateway con la hora del momento de la lectura.

Detalle de implementación verificado en el spike: el SDK devuelve los timestamps
en **hora local con offset**, no en UTC. La conversión a UTC se hace en el borde
de `Gateway.Da`; de ahí para adentro se asume cumplida. Dejarlo pasar sin
normalizar produciría saltos de una hora dos veces al año, imposibles de rastrear
meses después en un historiador.

En ese mismo borde se aplica la corrección de un bug del SDK cliente DA, que
recompone mal los 64 bits del `FILETIME` y devuelve el timestamp 429,4967296 s
atrasado cuando el bit 31 del campo bajo está prendido. El detalle está en
[`bug-filetime-sdk.md`](bug-filetime-sdk.md); acá importa una sola cosa: **la
corrección no es idempotente**, porque el valor corregido conserva el bit prendido y
una segunda pasada le sumaría otros siete minutos. Se aplica exactamente una vez, en
`SdkTimestamp.Correct()`, al construir el `TagSample`. De ahí para adentro el
timestamp se asume correcto y no se vuelve a tocar.

## Fuera de alcance de la PoC

- **Los bits de límite.** La especificación los mapea a los Limit Bits del
  `StatusCode` UA, así que la traducción es directa. Queda afuera porque no aporta
  a lo que la PoC quiere demostrar, no porque no se pueda.

Es una extensión de la traducción, no un cambio de diseño.


## Nota sobre una divergencia detectada

Un documento de diseño previo del proyecto mapeaba `Bad_NotConnected` a
`BadCommunicationError`. El código no lo sigue: aplica la tabla normativa de la
especificación OPC UA (Parte 8, Anexo A), que le hace corresponder
`BadNotConnected`.

Se resolvió a favor de la norma. `BadNotConnected` dice que no hay vínculo con la
fuente del dato; `BadCommunicationError` sugiere una falla de comunicación en
curso, que es un diagnóstico distinto y mandaría a revisar la red por algo que
puede ser un servidor apagado. Verificado en UaExpert durante la Fase 4: el código
publica `BadNotConnected [0x808A0000]`.