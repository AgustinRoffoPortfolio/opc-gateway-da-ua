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

**Arranque**

```powershell
dotnet run --project src/Gateway.Host
```

El servidor queda escuchando en `opc.tcp://localhost:4840/GatewayDaUa` y genera su
propio certificado en `pki/` la primera vez que corre. Se conecta con cualquier
cliente OPC UA; durante el desarrollo se usó UaExpert.

La configuración vive en `src/Gateway.Host/appsettings.json` y los tags en
`config/tags.example.csv`.

## Decisiones de diseño

- **El `SourceTimestamp` no se pisa nunca.** El timestamp que viene del DA llega
  intacto al cliente UA; el gateway solo pone el `ServerTimestamp`. Un timestamp
  que se refresca solo hace que un historiador registre datos que nunca
  existieron.
- **La jerarquía se deriva del nombre del tag**, no de un campo aparte. Dos
  fuentes de verdad para lo mismo divergen.
- **Ningún tipo del SDK de OPC DA cruza el borde de `Gateway.Da`.** Lo garantiza
  el grafo de referencias entre proyectos, no la disciplina.
- **`PlatformTarget x86` solo en el host.** El driver DA obliga a 32 bits, y el
  bitness lo define el ejecutable, no las bibliotecas.

El porqué de cada una, en [`docs/arquitectura.md`](docs/arquitectura.md).

## Roadmap

- [x] **Fase 0 — Spike de viabilidad del cliente DA.** Elección del SDK, servidor
      DA de simulación instalado, y un tag leído desde C# con valor, calidad y
      timestamp.
- [x] **Fase 1 — Esqueleto UA.** *(en curso)*
  - [x] Repositorio creado y verificado
  - [x] Core del servidor UA portado, corriendo en x86
  - [x] Address space construido desde el CSV, con jerarquía derivada de los puntos
  - [x] Documentación en `docs/`
- [ ] **Fase 2 — PoC vertical.** DA real → cache → nodo UA, con multiplicador y
      offset, preservando valor, calidad y timestamp de punta a punta.
- [ ] **Fase 3 — Motor de configuración robusto.** CSV extendido, validación
      acumulativa y carga parcial: un tag mal configurado queda fuera de servicio,
      no tira abajo el gateway.
- [ ] **Fase 4 — Resiliencia.** Caída y reconexión del servidor DA sin que el
      gateway se muera, con el estado degradado reflejado en los nodos UA.
- [ ] **Fase 5 — Diagnóstico.** Nodos UA de diagnóstico y página web de estado.
- [ ] **Fase 6 — Carga y validación cruzada.** Escalones hasta 10.000 tags, soak
      de 24 h, y comparación automática contra una referencia independiente.
- [ ] **Fase 7 — Seguridad y entrega.** Endpoints firmados y cifrados, usuarios y
      roles, servicio de Windows.

## Licencia

MIT. La licencia del repositorio quedó determinada por la del SDK cliente OPC DA
elegido en la Fase 0: [`TitaniumAS.Opc.Client.NetCore`](https://github.com/titanium-as/TitaniumAS.Opc.Client)
1.0.2.1, publicado bajo MIT. Si el SDK hubiera sido GPL, el repositorio sería GPL;
al ser permisivo, se optó por MIT para no imponer restricciones que la librería no
impone.