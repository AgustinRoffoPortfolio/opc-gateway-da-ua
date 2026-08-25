# Gateway OPC DA → UA

Expone un OPC DA Server legado detrás de un OPC UA Server moderno, sin tocar ni
migrar el sistema existente. Actúa como servidor OPC UA hacia los clientes y como
cliente OPC DA hacia el servidor legado, traduciendo valor, **calidad** y
**timestamp de origen** entre dos modelos de datos que no coinciden.

> **Alcance:** prueba de concepto. No es un producto y no va a producción.
>
> Gateways OPC DA→UA existen a montones, comerciales y libres. El valor de este no
> es la novedad: es la integración con un protocolo legado sobre COM, la
> traducción entre dos modelos distintos de calidad y tiempo, y la validación
> medida.

## Arquitectura

```
   ┌──────────────────────────────────────────────────────────┐
   │  MISMA MÁQUINA (sin DCOM remoto — restricción de diseño)  │
   │                                                          │
   │  ┌───────────────┐         ┌──────────────────────────┐  │
   │  │ OPC DA Server │◄──COM──►│  GATEWAY (proceso único) │  │
   │  │  (legado /    │  local  │                          │  │
   │  │   simulador)  │         │   OpcDaTagSource         │  │
   │  └───────────────┘         │          ↓               │  │
   │                            │      TagCache            │  │
   │                            │          ↓               │  │
   │                            │   NodeManager            │  │
   │                            │   + Address Space (CSV)  │  │
   │                            │          ↓               │  │
   │                            │   OPC UA Server          │  │
   │                            │                          │  │
   │                            │   Kestrel — página de    │  │
   │                            │   diagnóstico            │  │
   │                            └───────────┬──────────────┘  │
   └────────────────────────────────────────┼─────────────────┘
                                            │ OPC UA
                        ┌───────────────────┼───────────────────┐
                        ▼                   ▼                   ▼
                   Cliente UA           UaExpert            Historiador
```

La cache es el centro del diseño: desacopla la frecuencia de lectura DA de la
frecuencia de publicación UA, y de la cantidad de clientes conectados. Sin ella,
diez clientes preguntando lo mismo serían diez lecturas contra el servidor legado.
Con 4 clientes UA sobre los mismos 500 tags, la tasa de lectura DA no se movió
—0,989 a 0,987 ciclos/s— mientras las notificaciones UA se multiplicaban por 4,36.

Detalle completo en [`docs/arquitectura.md`](docs/arquitectura.md).

## Cómo se levanta

**Requisitos**

- Windows
- .NET 10 SDK
- Runtimes de .NET 10 en **x86** — hacen falta los dos, porque el instalador de
  ASP.NET Core no incluye el runtime base en su variante de 32 bits:
  `dotnet-runtime-win-x86` y `aspnetcore-runtime-win-x86`
- Un OPC DA Server. Durante el desarrollo se usó MatrikonOPC Server for
  Simulation and Testing.

**Arranque**

```powershell
dotnet run --project src/Gateway.Host
```

Quedan levantadas dos cosas:

- **El servidor OPC UA** en `opc.tcp://localhost:4840/GatewayDaUa`, que genera su
  propio certificado en `pki/` la primera vez que corre. Se conecta con cualquier
  cliente OPC UA; durante el desarrollo se usó UaExpert.
- **La página de diagnóstico** en `http://localhost:8080`, con dos vistas: una de
  operador (semáforo, estado del vínculo DA, contadores) y una de detalle (tabla
  de tags con buscador, y el `SourceTimestamp` contra el `LastUpdateUtc` en
  columnas contiguas).

Los dos endpoints escuchan solo en loopback, por decisión de diseño. El
procedimiento completo de arranque, el rollback y el ruido conocido en los logs
están en [`docs/operacion.md`](docs/operacion.md).

La configuración vive en `src/Gateway.Host/appsettings.json`. Los tags de ejemplo
están en `config/tags.example.csv`, y `config/aliases.example.csv` crea del lado
del simulador los ItemID que ese mapeo espera encontrar.

### Escenarios del simulador

Un escenario son dos piezas que tienen que coincidir: los aliases que existen del
lado DA y el CSV que los mapea a nombres UA. `config/demo-500.*` trae un escenario
de 500 tags ya armado, y `tools/Generate-LoadTestTags.ps1` genera uno de cualquier
tamaño:

```powershell
.\tools\Generate-LoadTestTags.ps1 -TagCount 4000
```

Eso escribe tres archivos en `scratch/`: el `.opcsim.xml` del escenario, el CSV de
tags para el gateway y el CSV de aliases (solo como respaldo, el XML ya los trae).

Para levantarlo:

1. En el configurador del simulador, `File → Open` sobre el `.opcsim.xml`. El
   panel de la izquierda tiene que mostrar la cantidad de aliases esperada.
2. Arrancar el gateway con el CSV de tags **de la misma corrida**:

```powershell
$env:Ua__TagsCsvPath = (Resolve-Path .\config\demo-500.tags.csv).Path
dotnet run --project src/Gateway.Host
```

El XML y el CSV se generan juntos y solo sirven de a pares: mezclar el XML de un
escenario con el CSV de otro da todos los tags en `Bad`, porque ninguno de los
ItemID que el CSV pide existe del lado DA.

Todos los aliases de un mismo tipo de dato apuntan al mismo ItemID nativo del
simulador (`Random.Real8`, `Random.Boolean`, `Random.Int4`, `Random.String`), así
que comparten el valor de origen. Es carga real para el lado UA —cada alias es un
item DA suscripto y un nodo UA publicado— pero no son señales independientes.

## Decisiones de diseño

- **El `SourceTimestamp` no se pisa nunca.** El timestamp que viene del DA llega
  intacto al cliente UA; el gateway solo pone el `ServerTimestamp`. Un timestamp
  que se refresca solo hace que un historiador registre datos que nunca
  existieron.
- **El contrato con la fuente de datos se partió en dos, no se ensanchó.** Con una
  cache en el medio hay dos preguntas distintas y no una más grande: el driver
  responde *qué acabo de leer* (`TagSample`) y el node manager pregunta *cuál es
  el último estado conocido* (`TagState`). Van a ritmos distintos y no comparten
  firma.
- **Una duda no se publica como `Bad`.** Un `DataValue` con `StatusCode` de master
  `Bad` no transporta valor, así que publicar una duda como `Bad` le borra al
  cliente el último dato bueno justo cuando lo necesita. La incertidumbre se
  expresa como `Uncertain`, que sí transporta valor y timestamp.
- **La jerarquía se deriva del nombre del tag**, no de un campo aparte. Dos
  fuentes de verdad para lo mismo divergen.
- **Ningún tipo del SDK de OPC DA cruza el borde de `Gateway.Da`.** Lo garantiza
  el grafo de referencias entre proyectos, no la disciplina.
- **`PlatformTarget x86` solo en el host.** El driver DA obliga a 32 bits, y el
  bitness lo define el ejecutable, no las bibliotecas.

Las 27 decisiones numeradas, con su porqué, están en
[`docs/decisiones.md`](docs/decisiones.md).

## Estado

**Implementación cerrada al 24/08/2026.** Siete fases ejecutadas —dos de ellas con
alcance recortado y el criterio de recorte declarado— y la octava descartada por
decisión. De acá en más el proyecto solo recibe refinamiento de documentación.

54 tests en verde. Lo que se verificó con los propios ojos, fase por fase, está en
[`docs/verificacion.md`](docs/verificacion.md); las mediciones de volumen, en
[`docs/pruebas-carga.md`](docs/pruebas-carga.md) y
[`docs/pruebas-carga-rendimiento.md`](docs/pruebas-carga-rendimiento.md).

- [x] **Fase 0 — Spike de viabilidad del cliente DA.** Elección del SDK, servidor
  DA de simulación instalado, y un tag leído desde C# con valor, calidad y
  timestamp.
- [x] **Fase 1 — Esqueleto UA.** Core del servidor UA portado y corriendo en x86,
  address space construido desde el CSV con jerarquía derivada de los puntos.
- [x] **Fase 2 — PoC vertical.** DA real → cache → nodo UA, con multiplicador y
  offset. Valor idéntico al último decimal entre el cliente DA y UaExpert, y
  `SourceTimestamp` idéntico al milisegundo.
- [x] **Fase 3 — Motor de configuración robusto.** CSV extendido, validación
  acumulativa y carga parcial: un CSV con cinco errores deliberados arranca
  igual, reporta los cinco y sirve el resto.
- [x] **Fase 4 — Resiliencia.** Caída del servidor DA detectada en ~2-3 s
  (objetivo < 10 s) y recuperada en ~6 s (objetivo < 30 s), sin caídas del
  gateway y con la degradación visible desde el cliente UA.
- [x] **Fase 5 — Diagnóstico.** Nodos UA de diagnóstico y página web de estado
  con vistas de operador y de detalle.
- [x] **Fase 6 — Carga y validación cruzada, con alcance recortado.** Cinco
  corridas documentadas:

  - **Escalones 500 / 4.000 / 8.000 tags:** sin errores, arranque del address
    space en menos de un segundo. Salvedad: los 8.000 aliases se alimentan de
    4 ItemID de origen, así que es el peor caso para el lado UA pero no son
    8.000 señales independientes.
  - **4 clientes UA simultáneos sobre los mismos 500 tags:** la tasa de lectura
    DA no se movió (0,989 → 0,987 ciclos/s) mientras las notificaciones UA se
    multiplicaban por 4,36, con memoria y handles planos. Es la tesis de la
    cache medida: los clientes UA no le llegan al servidor legado.
  - **Latencias de punta a punta:** DA→cache 6,2 ms de media (máx 24,9);
    cache→cliente 497,6 ms de mediana y 1025,7 ms de p95, dominadas por el
    intervalo de publicación de 1000 ms, no por el gateway.
  - **Soak de 2 h:** Private Bytes entre 51,9 y 54,1 MB sin tendencia. Los
    handles oscilan en diente de sierra entre 546 y 714 —los RCWs se liberan
    por el finalizador, no de forma determinística— pero sin crecimiento neto:
    el pico más alto es de los 22 minutos. Sin fuga de memoria ni de handles COM.
  - **Bug de `FILETIME` del SDK cliente DA** identificado, corregido y cubierto
    con tests ([docs/bug-filetime-sdk.md](docs/bug-filetime-sdk.md)).

  **Fuera de alcance por decisión:** el escenario de variación parcial, los
  soaks de 8 y 24 h, y la validación cruzada contra una referencia
  independiente. Con la tesis de la cache ya medida y sin fuga en 2 h, el
  esfuerzo restante rendía menos que cerrar el proyecto y presentarlo.

- [x] **Fase 7 — Seguridad y entrega, con alcance recortado.** Bind acotado a
  loopback —comprobado contra el socket, no contra la configuración—, solo
  lectura que hace cumplir el propio stack (`BadNotWritable`), validación
  estricta de certificados que rechaza de verdad, y auditoría de conexiones y
  rechazos en cinco escenarios, que encontró y corrigió dos falsos positivos.

  **Fuera de alcance por decisión:** servicio de Windows, y usuarios y roles.
  Una PoC atada a loopback no gana nada con autenticación de usuario, y
  empaquetarla como servicio es trabajo de producto, no de portfolio.
  **Declarado no corrido:** el rechazo por token de usuario. El contador existe
  y filtra por los `StatusCode` de identidad, pero no se provocó un rechazo real.



## Documentación

| Documento | Qué contiene |
|---|---|
| [`docs/arquitectura.md`](docs/arquitectura.md) | El diseño de corrido, y el índice del resto |
| [`docs/decisiones.md`](docs/decisiones.md) | Las decisiones numeradas con su porqué |
| [`docs/configuracion-tags.md`](docs/configuracion-tags.md) | El CSV campo por campo y la carga parcial |
| [`docs/calidad-da-ua.md`](docs/calidad-da-ua.md) | La tabla de mapeo de calidad DA ↔ StatusCode UA |
| [`docs/verificacion.md`](docs/verificacion.md) | Qué se comprobó con los propios ojos, fase por fase |
| [`docs/operacion.md`](docs/operacion.md) | Cómo se levanta y qué mirar si falla |
| [`docs/bug-filetime-sdk.md`](docs/bug-filetime-sdk.md) | El bug del SDK: aritmética, evidencia y corrección |
| [`docs/glosario.md`](docs/glosario.md) | La jerga del dominio |

## Licencia

MIT. La licencia del repositorio quedó determinada por la del SDK cliente OPC DA
elegido en la Fase 0: `TitaniumAS.Opc.Client.NetCore` 1.0.2.1, publicado bajo MIT.
Si el SDK hubiera sido GPL, el repositorio sería GPL; al ser permisivo, se optó por
MIT para no imponer restricciones que la librería no impone.

**El paquete que se usa no es el oficial.** El proyecto original,
[TitaniumAS.Opc.Client](https://github.com/titanium-as/TitaniumAS.Opc.Client),
targetea `net40` puro y no corre en .NET moderno. `...NetCore` 1.0.2.1 es un
repaquetado de un tercero (owner `MysticBoy`, publicado en septiembre de 2018, sin
repositorio de origen declarado en NuGet). Se eligió porque era la única vía para
consumir el SDK desde .NET 10 sin vendorizar el fuente.

Esa distinción no es un detalle de procedencia: es la explicación de por qué el
proyecto arrastró durante cinco fases un bug de conversión de `FILETIME` que
upstream ya había corregido en 2021 y que nunca se publicó en NuGet. El diagnóstico
completo está en [docs/bug-filetime-sdk.md](docs/bug-filetime-sdk.md).