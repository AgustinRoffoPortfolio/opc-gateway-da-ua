# Pruebas de carga — rendimiento

Las dos corridas que miden la tesis del diseño: que la cache aísla al servidor DA
legado de la cantidad de clientes UA conectados, y cuánto tarda un dato en cruzar
el gateway de punta a punta.

Las mediciones de **escala, memoria y soak** están en
[`pruebas-carga.md`](pruebas-carga.md), junto con los números a citar y una nota
transversal sobre el desfasaje de `SourceTimestamp` que aplica también a estas dos
corridas. La numeración es global a los dos documentos y cronológica, por eso acá
aparecen la 3 y la 4.

> Las condiciones de estas corridas mencionan el configurador de MatrikonOPC
> **abierto**. Se lo dejaba abierto por una hipótesis hoy descartada sobre el
> desfasaje de `SourceTimestamp`, que resultó ser un bug del SDK cliente DA
> ([`bug-filetime-sdk.md`](bug-filetime-sdk.md)). Para estas mediciones la
> condición es indistinta: ninguna usa `SourceTimestamp` como instrumento.

## Corrida 3 — 19/08/2026 — Múltiples clientes UA sobre los mismos tags

### Objetivo

Medir la afirmación central del diseño: la cache existe para que N clientes UA
pidiendo los mismos datos no se traduzcan en N veces el trabajo contra el
servidor DA legado. Estaba escrito en `arquitectura.md` como decisión, nunca
medido.

### Diseño del experimento

Se eligió el escalón de **500 tags** y no el de 8.000, y **los mismos 500 tags en
todos los clientes**. El motivo es aislar la variable: con 8.000 de fondo, el
gateway estaría haciendo un trabajo pesado ajeno al experimento, y con
subconjuntos distintos por cliente se estaría midiendo cantidad de
MonitoredItems en lugar de cantidad de clientes. La pregunta que se quiso
contestar es estrictamente "¿cuatro clientes preguntando exactamente lo mismo
multiplican la carga sobre el DA?".

La medición no necesitó instrumentación nueva: el contador `ReadCycles` ya
existía en `GatewaySnapshot` y se expone en `/api/diagnostics`.

### Herramienta: `tools/UaLoadClient`

Proyecto de consola **fuera de la solución** (no lo compila `dotnet build` de la
raíz), que abre N sesiones UA independientes contra el gateway, cada una con su
suscripción a los mismos tags, y cuenta las notificaciones recibidas por sesión.
Contar notificaciones no es decorativo: distingue un cliente que está recibiendo
datos de uno que solo está conectado.

Resuelve el índice del namespace **por URI** (`http://opc-gateway-da-ua/`) en vez
de hardcodear `ns=2`, que se rompería apenas cambie el orden de registro en el
servidor. Los NodeId de tag son el nombre completo del CSV
(`GatewayNodeManager.cs:319`).

Se ejecuta así:

```powershell
dotnet run --project tools\UaLoadClient -- opc.tcp://localhost:4840/GatewayDaUa 4 "ruta\tags-500.csv" 5
```

(endpoint · cantidad de clientes · CSV · minutos)

### Método de medición

Las dos ventanas se midieron igual: marca de `readCycles` **con timestamp** al
inicio y al final, y se compara la **tasa** (ciclos por segundo), no el total.
Un primer intento comparó totales y quedó sesgado porque los segundos que pasan
entre arrancar el cliente y tomar la marca no son los mismos en cada corrida;
comparar tasas cancela ese desfasaje.

Condiciones: 500 tags, `Da:UpdateRateMs = 1000`, diagnósticos y página web
habilitados, ventanas de 5 minutos, todo en la misma sesión sin reiniciar el
gateway entre ventanas.

### Resultados

| Métrica | 1 cliente | 4 clientes | Factor |
|---|---|---|---|
| Ciclos DA | 297 en 300,2 s | 295 en 299,0 s | — |
| **Tasa de lectura DA** | **0,989 /s** | **0,987 /s** | **×1,00** |
| Notificaciones UA recibidas | 110.100 | 480.268 | ×4,36 |
| Private Bytes (media) | 57,1 MB | 57,2 MB | ×1,00 |
| Working Set (media) | 64,0 MB | 65,2 MB | ×1,02 |
| Handles (media) | 668 | 671 | ×1,00 |

Reparto por cliente en la ventana de 4: 110.180 / 127.760 / 121.268 / 121.060.
Ningún cliente quedó servido de menos: cada uno recibió aproximadamente lo mismo
que recibía el cliente solo.

Las medias de memoria y handles salen de una corrida aparte de 5 minutos por
configuración (`memoria-clientes1.csv`, `memoria-clientes4.csv`), con los
clientes conectados durante toda la ventana de muestreo.

### Lectura de los resultados

**La cache hace lo que promete.** La tasa de lectura contra el DA es idéntica con
1 y con 4 clientes: 0,2% de diferencia, dentro del ruido. El gateway lee el
servidor legado a su propio ritmo configurado y los clientes UA se sirven de la
cache, sin llegar nunca al DA. Es el resultado que justifica la decisión de
diseño de `arquitectura.md`, y **es el número que conviene citar**: es la tesis
del proyecto demostrada.

**El costo del lado UA es real pero barato.** Las notificaciones se multiplicaron
por 4,36 mientras la memoria del proceso no se movió (0,1 MB) y los handles
subieron 3. Cuatro sesiones con 500 MonitoredItems cada una son 2.000 items
monitoreados y el gateway ni se inmuta.

**0,987 ciclos/s es prácticamente el 1 Hz configurado**, o sea que el trabajo por
ciclo (leer, escalar, cachear y publicar 500 tags) es despreciable frente al
intervalo de 1 segundo.

**Estabilidad de memoria con 500 tags.** Private Bytes se mantuvo entre 57,0 y
57,5 MB en las dos ventanas — prácticamente inmóvil, muy distinto del rango de
65-87 MB con 8.000 tags. Refuerza la conclusión de la corrida 2: esa oscilación
era el asentamiento del heap bajo carga alta, no memoria que el gateway soltara.

### Limitaciones de esta corrida

- **Cuatro clientes, no cuarenta.** Se demostró que la carga DA es independiente
  de la cantidad de clientes en el rango probado, no dónde está el techo del
  lado UA.
- **Clientes en la misma máquina.** Comparten CPU con el gateway y el servidor
  DA. No hay red de por medio.
- **Los clientes solo escuchan.** No hacen browse, ni lecturas puntuales, ni
  reconexiones. Es el caso de uso de suscripción sostenida.
- **Notificaciones al 86-88% del teórico.** 110.100 recibidas contra 500 × 297 =
  148.500 posibles. Sugiere que con el escenario de 500 no todos los tags cambian
  en todos los ciclos, a diferencia del de 8.000 donde todos vienen de la rama
  `Random`. No se investigó, pero es una pista para el escenario de variación
  parcial que quedó fuera del alcance de la Fase 6.

## Corrida 4 — 19/08/2026 — Latencias DA→cache y cache→cliente

### Objetivo

Cerrar el ítem 3 de la Fase 6, obligatorio para el reporte de cierre: cuánto
tarda un dato desde que el driver lo lee hasta que un cliente UA lo recibe.

Son dos tramos con naturaleza distinta y se miden con instrumentos distintos.

### Tramo DA→cache: ya estaba medido

El ciclo de adquisición (`DaAcquisitionService.Run`) cronometra desde antes de
`source.ReadAll()` hasta después de `_cache.Update(...)`, que es exactamente la
definición del tramo. Sale por `/api/diagnostics` como `lastCycleMs`, `avgCycleMs`
y `maxCycleMs`.

Lo único que se corrigió fue el instrumento: medía restando `DateTime.UtcNow`, que
en Windows avanza a saltos de ~15,6 ms. Los microsegundos que reportaba eran
precisión aparente — un ciclo de 6 ms solo podía dar 0 o 15.600 µs. Se pasó a
`Stopwatch`, que usa el contador de alta resolución del procesador.

### Tramo cache→cliente: la sonda

Cruza dos procesos, así que un `Stopwatch` no sirve. Tampoco sirve el
`ServerTimestamp`: lo estampa el stack UA en el momento del sampling, no cuando la
cache se actualizó, así que restarlo desde el cliente mediría casi cero.

La solución es un nodo sonda, `Gateway.Performance.CacheStampUtc`, cuyo *valor* es
la hora del gateway sellada en el hilo DA justo después de que la cache quedó
actualizada. Como los dos procesos comparten el reloj de la máquina, el cliente
calcula `UtcNow − sello` al recibir la notificación y obtiene la latencia real.

**Por qué no se tocó la semántica de timestamps para medir esto:** `node.Timestamp`
sigue siendo el `SourceTimestamp` del servidor DA, que es la tesis del proyecto. La
sonda es un nodo de diagnóstico aparte y no interfiere con los tags.

El sello viaja por el camino que ya existía: `DaLinkStatus` → `GatewaySnapshot` →
`GatewayPerformance` → nodo UA y página web. Sale como string ISO-8601 con cultura
invariante, no como `DateTime`, para que el cliente lo parsee sin depender de cómo
el stack convierta el tipo fecha.

### La sonda necesita su propia suscripción

Primer intento: la sonda compartía la suscripción de los 500 tags, con
`PublishingInterval` de 1000 ms. Resultado inflado — media 1098 ms, rango de 551 a
1569. Ese ancho de ~1000 ms exactos era la cola de publicación del propio cliente,
no latencia del gateway.

Con suscripción propia a 100 ms de publishing, la medición queda limpia. La
suscripción de los 500 tags se deja en 1000 ms a propósito: es la carga que se está
midiendo y cambiarla falsearía el conteo de notificaciones de la corrida 3.

### Configuración de la corrida

- 500 tags (`carga-500.opcsim.xml`), 1 cliente UA, 5 minutos.
- `UpdateIntervalMs` (publicación UA) 1000 · `UpdateRateMs` (lectura DA) 1000.
- Sonda: suscripción propia, publishing 100 ms, sampling 100 ms.
- `monitoredItems: 501` — los 500 tags más la sonda, en dos suscripciones.
- Configurador de MatrikonOPC abierto sin monitoreo activo (`Clients: 1`).

### Resultados

| Tramo | Métrica | Valor |
|---|---|---|
| DA→cache | media | 6,2 ms |
| | máx | 24,9 ms |
| cache→cliente | mín | 26,4 ms |
| | media | 537,3 ms |
| | p50 | 497,6 ms |
| | p95 | 1025,7 ms |
| | máx | 1111,2 ms |

297 muestras de latencia. 106.345 notificaciones recibidas por el cliente.

`avgCycleMs` pasó de 6,23 a 6,20 durante los 5 minutos con el cliente conectado, y
`maxCycleMs` quedó en 24,86 (valor previo a la corrida): la carga UA no degradó el
ciclo DA. Es evidencia adicional para la conclusión de la corrida 3.

### Lectura de los resultados

**La latencia cache→cliente no es costo de procesamiento, es espera de reloj.** El
dato ya está en la cache; lo que tarda es el próximo tick del timer de publicación.
Media 537 y mediana 498 sobre un ciclo de 1000 ms es una distribución uniforme: el
valor puede caer en cualquier punto del ciclo. El mínimo de 26 ms es el caso donde
el sello se escribió justo antes del tick.

**El trabajo real del gateway es dos órdenes de magnitud menor que esa espera** —
6 ms contra 537. Bajar `UpdateIntervalMs` reduce la latencia a costa de CPU: es una
palanca de configuración, no un límite del diseño.

### Limitaciones de esta corrida

- **La sonda es un nodo solo.** Mide el camino de publicación pero no compite con
  los otros 500 en la cola de notificación. Es una cota inferior, no el peor caso.
- **No se midió con 8.000 tags.** El tramo DA→cache sí escala con la cantidad
  (corrida 1), pero la latencia cache→cliente con carga alta queda sin medir.
- **Un solo cliente.** No se midió si la latencia se degrada con 4 clientes.