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

La configuración vive en `src/Gateway.Host/appsettings.json`. Los tags de ejemplo
están en `config/tags.example.csv`, y `config/aliases.example.csv` crea del lado
del simulador los ItemID que ese mapeo espera encontrar.

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

Las veinte y pico de decisiones numeradas, con su porqué, están en
[`docs/decisiones.md`](docs/decisiones.md).

## Estado

54 tests en verde. Lo que se verificó con los propios ojos, fase por fase, está en
[`docs/verificacion.md`](docs/verificacion.md); las mediciones de volumen, en
[`docs/pruebas-carga.md`](docs/pruebas-carga.md).

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
- [~] **Fase 6 — Carga y validación cruzada.** *(parcial)* Corrida de escalones
      500 / 4.000 / 8.000 tags: sin errores, arranque del address space en menos
      de un segundo, memoria plana en ~73 MB y sin crecimiento en un soak de 33
      minutos. Con la salvedad de que los 8.000 aliases se alimentan de 4 ItemID
      de origen, así que la carga del lado UA es el peor caso pero no son 8.000
      señales independientes. **Faltan** el escenario de variación parcial, los
      clientes UA múltiples, los soaks de 8 y 24 h, la medición de latencias
      DA→cache y cache→cliente, y la validación cruzada contra una referencia
      independiente.
- [ ] **Fase 7 — Seguridad y entrega.** Endpoints firmados y cifrados, usuarios y
      roles, servicio de Windows.
- [ ] **Fase 8 — Opcional.** Recarga de configuración en caliente y escritura
      UA → DA. El gateway es de solo lectura hasta que alguien lo pida.

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