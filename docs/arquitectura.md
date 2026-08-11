# Arquitectura

## Qué es

Un gateway que expone un OPC DA Server legado detrás de un OPC UA Server moderno.
Actúa como **servidor OPC UA** hacia los clientes y como **cliente OPC DA** hacia
el servidor existente. En el medio hay una cache propia, un mapeo de nombres
configurable por CSV, conversión de unidades, y traducción explícita de calidad y
timestamp.

Es una PoC. No es un producto y no va a producción.

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
   │                            │            │             │  │
   │                            │  ┌─────────▼──────────┐  │  │
   │                            │  │   TagCache         │  │  │
   │                            │  │   (frontera entre  │  │  │
   │                            │  │    los dos mundos) │  │  │
   │                            │  └─────────┬──────────┘  │  │
   │                            │            │             │  │
   │                            │  ┌─────────▼──────────┐  │  │
   │                            │  │  NodeManager       │  │  │
   │                            │  │  + Address Space   │  │  │
   │                            │  │  (desde CSV)       │  │  │
   │                            │  └─────────┬──────────┘  │  │
   │                            │            │             │  │
   │                            │  ┌─────────▼──────────┐  │  │
   │                            │  │  OPC UA Server     │  │  │
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

## Estructura de proyectos

```
src/
├── Gateway.Core/   # cache, configuración, transformación
├── Gateway.Da/     # cliente DA + OpcDaTagSource
├── Gateway.Ua/     # server core, node manager, address space
├── Gateway.Web/    # Kestrel + diagnóstico
└── Gateway.Host/   # composición y arranque
```

El grafo de referencias es deliberado: `Core` no referencia a nadie, y `Ua` no
referencia a `Da`. Eso hace que dos reglas de arquitectura las imponga el
compilador en vez de la disciplina personal:

- **Ningún tipo del SDK de OPC DA cruza el borde de `Gateway.Da`.** Salen tipos
  propios del gateway (enum de calidad, `DateTime`, valor convertido), nunca un
  tipo del SDK. Si el SDK se reemplaza, el cambio queda contenido en un proyecto.
- **El node manager no sabe de dónde salen los datos.** Habla con la cache de
  `Core`. Ignoró Modbus en el proyecto anterior e ignora COM y OPC DA acá, sin
  que haya hecho falta tocarlo.

## Decisiones de diseño

### 1. La interfaz de la fuente de datos se ensancha

El proyecto anterior (`oilfield-scada`) define la fuente de valores así:

```csharp
void Step(double dtSeconds);
bool TryGetValue(string tagName, out double value);
```

Alcanzaba para Modbus, donde la calidad es binaria: contestó o no contestó. Para
OPC DA no alcanza, por tres razones distintas:

- **`out double` no soporta `String`.** El CSV declara `Double`, `Boolean`,
  `Int32` y `String`. El tipo del valor tiene que abrirse, no solo sumarle campos.
- **Falta la calidad y el `SourceTimestamp`.** OPC DA entrega valor, calidad y
  timestamp de origen por cada item, y los tres tienen que llegar intactos al
  cliente UA.
- **El `bool` de retorno se vuelve redundante.** Con un `StatusCode` en la
  respuesta, "no conozco ese tag" deja de ser `false` y pasa a ser un status
  no-bueno. Hoy un tag desconocido y uno con calidad mala son indistinguibles;
  en un gateway no pueden serlo.

La firma nueva es del tipo `(valor, StatusCode, SourceTimestamp)`.

### 2. El `SourceTimestamp` nunca se pisa

El `ServerTimestamp` lo pone el gateway. El `SourceTimestamp` es el que vino del
DA y no se sobrescribe en ningún punto del recorrido.

El node manager heredado hacía `node.Timestamp = DateTime.UtcNow` en cada ciclo
de publicación. Eso es correcto para un simulador y **corrupción de datos** para
un gateway: si un tag no cambia hace diez minutos, su timestamp tiene que tener
diez minutos de antigüedad. Un timestamp que se refresca solo hace que un
historiador aguas abajo registre diez minutos de datos que nunca existieron, y
ese problema aparece meses después y es carísimo de rastrear.

La verificación es visual: en el árbol de prueba, un solo tag cambia de valor
periódicamente y su `SourceTimestamp` avanza; los demás quedan clavados en la
hora de arranque mientras su `ServerTimestamp` sigue actualizándose.

### 3. La jerarquía se deriva del nombre del tag

El punto en `TAG_NAME_OPC_UA` separa jerarquía: `PLANTA_01.MEDICION.PRESION_ENTRADA`
produce dos carpetas anidadas y una variable. **No existe un campo `NODE_PATH`**
en el CSV, deliberadamente: sería una segunda fuente de verdad para lo mismo, y
dos fuentes de verdad divergen.

Se usa el punto y no la barra porque en el proyecto anterior la `/` en los
nombres rompió el ruteo de un endpoint HTTP aguas abajo, que terminó necesitando
el tag por query string.

Carpetas intermedias compartidas se crean una sola vez, indexadas por su ruta
acumulada. Dos tags que comparten prefijo cuelgan de la misma carpeta, y dos
carpetas homónimas en ramas distintas no colisionan porque el `NodeId` es la
ruta completa, no el segmento.

### 4. `BaseDataVariableState` antes que `AnalogItemState`

El proyecto anterior usa `AnalogItemState`, que exige `EURange` y
`EngineeringUnits`. El CSV en su versión PoC no trae ni unidad ni rango, y
además no existe `AnalogItemState` para `String`.

Se arranca con `BaseDataVariableState`, que acepta cualquier `DataType`, y se
sube a `AnalogItemState` más adelante para los tags que declaren unidad de
ingeniería. Subir es fácil; arrancar con un tipo que no admite uno de los cuatro
tipos de dato no.

### 5. El `PlatformTarget x86` va solo en el host

El driver de OPC DA obliga a un proceso de 32 bits. El `PlatformTarget` manda en
el ejecutable: si el host es x86, todas las bibliotecas que cargue corren en ese
proceso aunque estén compiladas como `AnyCPU`.

Por eso el x86 está en `Gateway.Host` y nada más. Ponerlo en los cinco proyectos
ataría las manos sin beneficio y complicaría una eventual partición en dos
procesos.

**Consecuencia a vigilar:** un proceso de 32 bits tiene un límite práctico de
memoria útil cercano a los 2 GB. No molesta con cientos de tags; puede apretar
en las pruebas de carga con decenas de miles.

### 6. La ubicación de la PKI se fija en el primer arranque

El stack OPC UA genera su propio certificado la primera vez que corre. **Cada
mudanza de la carpeta PKI regenera el certificado y rompe toda la confianza ya
establecida con los clientes.**

Por eso la carpeta se resuelve por ruta absoluta anclada al archivo de solución,
subiendo desde la ubicación del ejecutable — nunca contra el working directory.
Si se resolviera contra el working directory, arrancar con `dotnet run` desde la
raíz y ejecutar el binario desde su carpeta de salida darían dos carpetas PKI
distintas, y el certificado se regeneraría al alternar entre las dos formas.

## Configuración

Todo lo configurable vive fuera del código:

- **`appsettings.json`** — endpoint, namespace, intervalo de publicación, ruta de
  la PKI y ruta del CSV de tags.
- **`config/tags.csv`** — la definición de los tags.

Al repositorio va únicamente `config/tags.example.csv`, con nombres de tags,
dispositivos y servidores **inventados**. El archivo real queda fuera del control
de versiones.

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

Lo que el port confirmó: la abstracción de la fuente de valores estaba bien
puesta. Apareció un protocolo que no estaba previsto cuando se diseñó, y entra
como una implementación más, sin tocar el node manager.

## Estado de verificación

Cerrado durante la Fase 1:

- **El stack OPC UA funciona en 32 bits.** Servidor levantado, certificado
  generado y cliente externo conectado, todo con el proceso en x86.
- **La profundidad arbitraria del árbol funciona.** El proyecto anterior nunca
  pasó de dos niveles. Se verificó con un tag de cuatro niveles
  (`PLANTA_01.MEDICION.CAUDAL.TOTALIZADO`): las carpetas anidadas se navegan sin
  problema desde un cliente externo.
- **Los cuatro tipos de dato se publican correctamente** (`Double`, `Boolean`,
  `Int32`, `String`), verificado leyendo desde un cliente externo.

Abierto:

- El resolvedor de la raíz del repositorio usa el archivo de solución como ancla
  y falla ruidosamente si no lo encuentra. Publicado como servicio, ese archivo
  no va a existir, así que la ubicación de la PKI tendrá que definirse de forma
  explícita. Falla ruidosa a propósito: es preferible a que un servicio arranque
  contra una carpeta arbitraria y regenere el certificado en silencio.

- **Requisitos de arranque del cliente DA (Fase 2).** La librería exige que
  `Bootstrap.Initialize()` se llame lo más arriba posible del arranque, antes de
  construir cualquier host, y que el proceso corra en apartment **MTA** por la
  llamada a `CoInitializeSecurity` que hace internamente. Las aplicaciones de
  consola de .NET son MTA por defecto, pero conviene verificarlo explícitamente
  antes de dar por bueno el primer arranque con COM.