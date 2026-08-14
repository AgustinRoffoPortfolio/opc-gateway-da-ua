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
| [operacion.md](operacion.md) | Cómo se levanta y qué mirar si falla |
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

## Configuración

Todo lo configurable vive fuera del código:

- **`appsettings.json`** — endpoint, namespace, intervalo de publicación, ruta de
  la PKI y ruta del CSV de tags.
- **`config/tags.csv`** — la definición de los tags. El formato completo, la
  política de carga parcial y las decisiones detrás de cada campo están en
  [configuracion-tags.md](configuracion-tags.md).

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