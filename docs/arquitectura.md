# Arquitectura

## Qué es

Un gateway que expone un OPC DA Server legado detrás de un OPC UA Server moderno.
Actúa como **servidor OPC UA** hacia los clientes y como **cliente OPC DA** hacia
el servidor existente. En el medio hay una cache propia, un mapeo de nombres
configurable por CSV, conversión de unidades, y traducción explícita de calidad y
timestamp.

Es una PoC. No es un producto y no va a producción.

## Cómo está organizada la documentación

Este archivo cuenta el diseño de corrido: qué hace el gateway, cómo está armado
y por qué las piezas están donde están. Lo que se consulta de forma puntual vive
aparte:

| Documento | Qué contiene |
|---|---|
| [decisiones.md](decisiones.md) | Las decisiones de diseño numeradas, con su porqué |
| [configuracion-tags.md](configuracion-tags.md) | El CSV campo por campo y la política de carga parcial |
| [calidad-da-ua.md](calidad-da-ua.md) | La tabla de mapeo de calidad DA a StatusCode UA |
| [verificacion.md](verificacion.md) | Qué se comprobó con los propios ojos, fase por fase |
| [pruebas-carga.md](pruebas-carga.md) | Las mediciones de volumen, con sus límites |
| [operacion.md](operacion.md) | Cómo se levanta y qué mirar si falla |
| [bug-filetime-sdk.md](bug-filetime-sdk.md) | El bug de conversión de `FILETIME` del SDK cliente DA, su corrección y la evidencia |
| [glosario.md](glosario.md) | La jerga del dominio |

## Diagrama

```
   ┌──────────────────────────────────────────────────────────┐
   │  MISMA MÁQUINA (sin DCOM remoto — restricción de diseño)  │
   │                                                          │
   │  ┌───────────────┐         ┌──────────────────────────┐  │
   │  │ OPC DA Server │◄──COM──►│  GATEWAY (proceso único) │  │
   │  │  (legado /    │  local  │                          │  │
   │  │   simulador)  │         │  ┌────────────────────┐  │  │
   │  └───────────────┘         │  │  OpcDaTagSource    │  │  │
   │                            │  │  (driver DA)       │  │  │
   │                            │  └─────────┬──────────┘  │  │
   │                            │            │ TagSample   │  │
   │                            │  ┌─────────▼──────────┐  │  │
   │                            │  │   TagCache         │  │  │
   │                            │  │   (frontera entre  │  │  │
   │                            │  │    los dos mundos) │  │  │
   │                            │  └─────────┬──────────┘  │  │
   │                            │            │ TagState    │  │
   │                            │  ┌─────────▼──────────┐  │  │
   │                            │  │  NodeManager       │  │  │
   │                            │  │  + Address Space   │  │  │
   │                            │  │  (desde CSV)       │  │  │
   │                            │  └─────────┬──────────┘  │  │
   │                            │            │             │  │
   │                            │  ┌─────────▼──────────┐  │  │
   │                            │  │  OPC UA Server     │  │  │
   │                            │  └────────────────────┘  │  │
   │                            │  ┌────────────────────┐  │  │
   │                            │  │  Kestrel — página  │  │  │
   │                            │  │  de diagnóstico    │  │  │
   │                            │  └────────────────────┘  │  │
   │                            └───────────┬──────────────┘  │
   └────────────────────────────────────────┼─────────────────┘
                                            │ OPC UA
                        ┌───────────────────┼───────────────────┐
                        ▼                   ▼                   ▼
                   Cliente UA           UaExpert            Historiador
```

## Por qué la cache es el centro

Todo pasa por la cache, y existe para desacoplar cuatro cosas que no tienen por
qué ir al mismo ritmo: la frecuencia de lectura DA, la frecuencia de publicación
UA, la cantidad de clientes UA conectados, y la transformación de unidades.

Si el gateway leyera del DA en respuesta a cada request UA, diez clientes
preguntando lo mismo serían diez lecturas contra el servidor legado. La cache
convierte eso en una sola lectura periódica, independiente de cuántos clientes
haya del otro lado.

Esa frontera también define qué tipo de dato viaja de cada lado. Del driver a la
cache viaja un `TagSample`: lo que se acaba de leer, o nada. De la cache al node
manager viaja un `TagState`: el último estado conocido, que puede ser viejo y que
lleva su propia antigüedad encima. Son dos preguntas distintas y por eso son dos
tipos distintos —el desarrollo de ese razonamiento está en la decisión 7.

## Estructura de proyectos

```
src/
├── Gateway.Core/   # cache, configuración, transformación
├── Gateway.Da/     # cliente DA + OpcDaTagSource
├── Gateway.Ua/     # server core, node manager, address space
├── Gateway.Web/    # Kestrel + diagnóstico
└── Gateway.Host/ # composición y arranque

tools/
└── TimestampProbe/ # experimento reproducible del bug de FILETIME


`tools/` es una desviación consciente de la estructura estándar del portfolio, que
solo contempla `src/` y `tests/`. Un experimento reproducible no es ninguna de las
dos cosas: no es código del producto, y no es un test porque su salida es un CSV
para analizar, no un verde o un rojo. Vale conservarlo porque la evidencia de un
diagnóstico se tiene que poder volver a generar, no solo leer.

El grafo de referencias es deliberado: `Core` no referencia a nadie, y `Ua` no
referencia a `Da`. Eso hace que dos reglas de arquitectura las imponga el
compilador en vez de la disciplina personal:
```

El grafo de referencias es deliberado: `Core` no referencia a nadie, y `Ua` no
referencia a `Da`. Eso hace que dos reglas de arquitectura las imponga el
compilador en vez de la disciplina personal:

- **Ningún tipo del SDK de OPC DA cruza el borde de `Gateway.Da`.** Salen tipos
  propios del gateway (enum de calidad, `DateTime`, valor convertido), nunca un
  tipo del SDK. Si el SDK se reemplaza, el cambio queda contenido en un proyecto.

  El bug de `FILETIME` ([bug-filetime-sdk.md](bug-filetime-sdk.md)) marcó hasta
  dónde llega esa garantía. El SDK nunca devolvió un tipo propio: devolvía un
  `DateTime` de .NET perfectamente válido, con siete minutos de menos. El borde
  aísla los *tipos* de la dependencia, no la *corrección de sus valores*, y por eso
  la corrección vive justo ahí, en el mismo lugar donde ya se normalizaba la zona
  horaria. Un borde que traduce tipos es también el único lugar sensato para
  compensar lo que la dependencia hace mal.
- **El node manager no sabe de dónde salen los datos.** Habla con la cache de
  `Core`. Ignoró Modbus en el proyecto anterior e ignora COM y OPC DA acá, sin
  que haya hecho falta tocarlo.

## Configuración

Todo lo configurable vive fuera del código:

- **`appsettings.json`** — endpoint OPC UA, namespace, intervalo de publicación,
  ruta de la PKI, ruta del CSV de tags y dirección de escucha de la página de
  diagnóstico.
- **`config/tags.csv`** — la definición de los tags. El formato completo, la
  política de carga parcial y las decisiones detrás de cada campo están en
  [configuracion-tags.md](configuracion-tags.md).

Al repositorio van dos archivos de ejemplo, con nombres de tags, dispositivos y
servidores **inventados**: `config/tags.example.csv`, que mapea UA↔DA para el
gateway, y `config/aliases.example.csv`, que crea del lado del simulador los
ItemID que ese mapeo espera encontrar. Los archivos reales quedan fuera del
control de versiones.

Dos reglas de formato que no son negociables:

- **Los intervalos son enteros en milisegundos**, con la unidad en el nombre de
  la clave (`UpdateIntervalMs`). En cultura es-AR, un binder interpreta `0,5` y
  `0.5` de forma distinta; con enteros el problema no existe.
- **El CSV se parsea con cultura invariante**, así que los decimales van con
  punto. El separador de campos es `;`. Con coma decimal y punto y coma de
  separador, esto se rompe solo si no se fuerza la cultura.

## Relación con `oilfield-scada`

Del proyecto anterior se copiaron el core del servidor UA, el node manager, la
gestión de certificados y el resolvedor de rutas de configuración. **Los dos
repositorios divergen desde el día uno y no se vuelven a sincronizar.**

Es deliberado: mantener un core compartido vía paquetes o submódulos es
sobre-ingeniería para esta etapa. Si algo mejora acá y sirve allá, se porta a
mano y se decide caso por caso.

Lo que el port dejó claro es que las dos abstracciones heredadas no corrieron la
misma suerte, y la diferencia es el resultado más interesante del proyecto.

**La del node manager sobrevivió intacta.** Apareció un protocolo que no estaba
previsto cuando se diseñó —COM, de los años noventa, con un modelo de calidad
propio— y el node manager no se tocó. Sigue hablando con una cache y sigue sin
saber quién la llena.

**La de la interfaz no sobrevivió.** `ITagValueSource` devolvía un valor y dejaba
que la calidad la decidiera el node manager, lo cual alcanzaba para Modbus, donde
la calidad es binaria: contestó o no contestó. Con OPC DA hay que transportar
valor, calidad y timestamp de origen, así que el plan era ensanchar la firma. Al
implementarlo apareció que con una cache en el medio no hay una pregunta más
grande sino dos preguntas distintas, y la interfaz común se partió en dos tipos de
dato (`TagSample` y `TagState`).

O sea que la abstracción correcta resultó ser el tipo y no la interfaz. Vale la
pena decirlo así y no como si todo hubiera estado bien puesto desde el principio:
lo que se sostuvo fue la decisión de que el node manager no supiera de protocolos,
y lo que se cayó fue suponer que un solo contrato podía servir a los dos lados de
una cache. Las decisiones 1 y 7 conservan el razonamiento en los dos momentos, la
primera marcada como superada por la segunda.