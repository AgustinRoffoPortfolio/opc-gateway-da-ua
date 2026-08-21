# Pruebas de carga

Mediciones de volumen del gateway. Cada corrida agrega filas a la tabla de
resultados; este documento no se reemplaza, se extiende.

## Corrida 1 — 14/08/2026 — Escalones 500 / 4.000 / 8.000

### Objetivo

Validar que el gateway sostiene la traducción DA→UA de ~8.000 tags de punta a
punta, de endpoint a endpoint. No se mide el simulador ni el stack UA por
separado: se mide el gateway completo bajo carga.

Parámetros de origen del pedido: 200 equipos × 40 tags = 8.000 tags.

### Por qué aliases y no los ItemID nativos

El simulador MatrikonOPC expone unos pocos ItemID nativos por rama. La
alternativa barata era apuntar los 8.000 tags UA directamente a los 4 ItemID de
la rama `Random`, pero en ese caso el servidor DA administra 4 items y la mitad
DA de la medición no existe.

Con aliases, el servidor DA administra 8.000 items reales aunque el valor por
debajo salga de la misma fuente. Es lo que hace que la medición sea del gateway
y no del stack UA solo.

### Esquema de nombres

- **UA:** `PLANTA_{NN}.EQUIPO_{NNN}.{ROL}` — el punto es la jerarquía del árbol UA.
- **DA:** `.PLANTA_{NN}_EQUIPO_{NNN}_{ROL}` — guiones bajos y punto inicial.

Los separadores difieren porque Matrikon rechaza puntos, comas y `#` en el
nombre de un alias: en OPC DA el punto es el separador de jerarquía del ItemID y
un alias con puntos sería ambiguo para el servidor. El punto inicial existe
porque el alias vive en el grupo raíz, así que el separador de jerarquía queda
huérfano adelante.

Que los dos nombres difieran es precisamente para lo que existe la columna de
mapeo del CSV.

- 200 equipos numerados globalmente (`EQUIPO_001` a `EQUIPO_200`).
- Planta derivada del número de equipo, 20 equipos por planta.
- Mezcla de tipos por equipo: 25 Double, 10 Boolean, 4 Int32, 1 String.

### Cómo se reproduce

Los dos CSV (aliases DA y tags UA) salen de una sola corrida del generador, para
que sean consistentes por construcción:

```powershell
.\tools\Generate-LoadTestTags.ps1 -TagCount 8000
```

Escribe `aliases-8000.csv` y `tags-8000.csv` en `scratch/`. Después:

1. En **MatrikonOPC Server for Simulation and Testing**: `File → Import Aliases`,
   elegir el CSV de aliases. Guardar la configuración con `File → Save As...`.
2. Levantar el gateway apuntándolo al CSV de tags:

```powershell
$env:Ua__TagsCsvPath = "C:\...\scratch\tags-8000.csv"
dotnet run --project src\Gateway.Host
Remove-Item Env:\Ua__TagsCsvPath
```

3. Conectar UaExpert a `opc.tcp://localhost:4840/GatewayDaUa` y arrastrar tags al
   Data Access View. Sin cliente suscripto, el servidor publica al vacío y la
   mitad UA de la medición no existe.

### Configuración de la corrida

| Parámetro | Valor |
|---|---|
| `Da.UpdateRateMs` | 1000 |
| `Ua.UpdateIntervalMs` | 1000 |
| Proceso | x86 (obligado por el driver DA) |
| Simulador | MatrikonOPC Simulation, misma máquina |
| Cliente UA | UaExpert, 1 sesión |

El pedido original mencionaba un intervalo de 10 s. Se midió a 1 s a propósito:
a 10 s el gateway hace una décima parte del trabajo por unidad de tiempo, y eso
puede tapar exactamente el problema que la prueba busca encontrar. Si aguanta a
1 s, aguanta a 10 s.

### Resultados

| Escalón | Tags válidos | Errores | Arranque address space | Memoria proceso | CPU proceso | Tags suscriptos UA |
|---|---|---|---|---|---|---|
| 500 | 500 | 0 | < 1 s | no medido | — | 1 |
| 4.000 | 4.000 | 0 | < 1 s | 73,7 MB | 0 % | 40 |
| 8.000 | 8.000 | 0 | < 1 s | 72,7 MB | 0,4 % | 800 |

Soak de 33 minutos en el escalón de 8.000, con los 800 tags suscriptos activos
durante toda la corrida:

| Momento | Memoria `Gateway.Host` | Memoria COM Surrogate |
|---|---|---|
| Inicio | 72,7 MB | 0,6 MB |
| +33 min | 73,2 MB | 1,0 MB |

Import de aliases en Matrikon: 500 casi instantáneo, 4.000 en 2-3 segundos,
8.000 sin cronometrar pero del mismo orden. El simulador no es el cuello de
botella.

### Lectura de los resultados

**La memoria es plana entre 4.000 y 8.000 tags.** La mayor parte de esos ~73 MB
es el runtime .NET y el stack UA cargados; las estructuras por tag son chicas y
duplicar la cantidad no mueve la aguja. El techo práctico de ~2 GB del proceso
x86 no está cerca de ser un problema en este orden de magnitud.

**El soak no muestra crecimiento.** Medio megabyte en 33 minutos con 8.000 items
COM leyéndose cada segundo es ruido del recolector de basura, no la pendiente
sostenida que tendría una fuga de handles COM. El crecimiento del COM Surrogate
(400 KB) queda anotado como observación menor: es el proceso que hospeda el
servidor DA, no el gateway.

**La CPU no llega a registrarse.** El trabajo por ciclo es tan corto frente al
segundo de espera que el muestreo del administrador de tareas lo redondea a
cero. A 1 s de intervalo el gateway está holgado.

### Verificación funcional durante la carga

No solo se midió consumo; se verificó que la traducción siguiera siendo
correcta bajo carga:

- **Escalado:** `VISCOSIDAD` con multiplicador `0.01` mostró 5701,039 en el
  cliente DA y 57,0103 en UaExpert, mismo instante.
- **Tipos:** los cuatro tipos (Double, Boolean, Int32, String) llegaron con su
  `DataType` correcto al cliente UA.
- **StatusCode:** `Good` en la totalidad de los tags observados.
- **SourceTimestamp:** los tags Boolean mostraron un `SourceTimestamp` distinto
  al de los Double en el mismo instante de lectura, correspondiente a sus
  ItemID de origen. Es decir que el gateway preserva el timestamp de origen por
  item y no estampa todo con la hora del ciclo, que es el comportamiento que el
  proyecto se propone garantizar.

## Limitaciones de esta corrida

Lo que esta medición **no** demuestra:

- **La fuente de datos son 4 ItemID.** Los 8.000 aliases apuntan a los 4 ItemID
  de la rama `Random`, así que todos los tags de un mismo tipo comparten el
  valor crudo. Para el gateway el trabajo por ciclo es el mismo (leer, escalar,
  cachear y publicar 8.000 entradas), y del lado UA es el peor caso: los 8.000
  cambian en el mismo ciclo y el servidor arma 8.000 notificaciones de una. Pero
  no son 8.000 señales independientes y no debe presentarse como tal.
- **No ejercita el dirty-check.** Como todos los valores cambian en cada ciclo,
  un filtro de "solo publicar lo que cambió" no descartaría nada y no quedaría
  medido. El escenario de variación parcial (usando la rama estática
  `Bucket Brigade`) queda pendiente para Fase 6.
- **Un solo cliente UA.** El escenario de múltiples clientes simultáneos, que es
  parte del sentido de tener una cache en el medio, no se midió.
- **Soak corto.** 33 minutos detectan una fuga grosera. Los soaks de 8 y 24 h del
  roadmap son otra cosa.
- **Sin latencia medida.** No se instrumentaron las latencias DA→cache ni
  cache→cliente UA; solo consumo de recursos y corrección funcional.
- **CPU compartida.** El gateway comparte máquina con el servidor DA. Los
  números de CPU son de presupuesto compartido, no propio.
- **Escalón de 500 sin memoria medida.**

## Asuntos abiertos

**`Oops! MonitoredItems queued but no notifications available` — cosmético.**
Durante la corrida de 8.000 el gateway registró este mensaje a nivel ERROR cinco
veces en 39 minutos, sin patrón regular. No apareció en los escalones de 500 ni
4.000, y la primera ocurrencia coincide con el salto de 40 a 800 tags
suscriptos.

El mensaje viene del lado servidor del stack de la OPC Foundation
(`Opc.Ua.Server`), no del código del gateway: al armar el `NotificationMessage`,
la suscripción tiene MonitoredItems marcados como listos para publicar pero la
lista de notificaciones sale vacía. El servidor descarta ese mensaje vacío y
sigue. El "Oops!" es el comentario literal de los autores para un caso que
consideran que no debería ocurrir.

Severidad medida: se habilitaron los nodos de diagnóstico del servidor
(`ServerDiagnostics`) y se reprodujo el escenario de 8.000 con 800 suscriptos.
Con el panel de log de UaExpert vaciado, se dejó correr hasta capturar una
ocurrencia del mensaje. **El cliente no registró ningún evento**: ni número de
secuencia salteado, ni republish, ni keep-alive fallido, y los valores siguieron
llegando frescos. Conclusión: no hay pérdida de datos observable por el cliente.

Lo que esta verificación **no** demuestra:

- Que el servidor esté internamente correcto. Se probó comportamiento
  observable, no corrección interna del stack.
- Que no se saltee ninguna muestra intermedia. UaExpert suscribe con
  `QueueSize=1, DiscardOldest=1`, o sea que pide solo el último valor: aunque se
  perdiera una muestra intermedia no lo notaría. Para un gateway que expone
  estado actual eso es aceptable; para un cliente que historice con colas más
  grandes, la pregunta habría que rehacerla.

Causa probable: `GatewayNodeManager.UpdateValues` escribe los 8.000 nodos en
cada ciclo y llama a `ClearChangeMasks` en todos, sin comparar contra el valor
anterior. Con la fuente `Random` los 8.000 cambian siempre, así que es el
escenario de máxima actividad de notificación por nodo. Un dirty-check —no
notificar cuando el estado no cambió— reduciría esa actividad, pero **no puede
medirse con esta configuración**, justamente porque acá todos los valores
cambian en cada ciclo. Queda anotado como optimización pendiente, no como
corrección de un bug.

**Nota sobre la configuración — resuelto en la corrida 2.** Los números de esta
tabla se midieron con los diagnósticos del servidor **deshabilitados** (el default
del stack). Se habilitaron después y quedaron habilitados en el código, así que
esta nota advertía que una corrida futura no sería directamente comparable, y
señalaba como evidencia los 148,7 MB medidos en Fase 5 contra los 72,7 MB de acá.

La corrida 2 midió las dos configuraciones en la misma sesión y **la advertencia
resultó equivocada**: el costo de los diagnósticos es de 4,2 MB (5,6%), dentro del
ruido de la medición. Lo que fallaba era la métrica, no la configuración —Working
Set oscila ±25 MB solo, por el recorte del sistema operativo. Los 72,7 MB de esta
tabla caen dentro del rango observado para esa misma configuración; los 148,7 MB
de Fase 5 no se reprodujeron. Ver [Corrida 2](#corrida-2--18082026--memoria-dos-configuraciones-en-la-misma-sesión).

**Desfasaje del SourceTimestamp (~7 min) — resuelto, es del simulador.** Durante
esta corrida el `SourceTimestamp` que llegaba a UaExpert estaba desfasado unos 7
minutos y 10 segundos respecto del `ServerTimestamp`. El valor avanzaba
correctamente ciclo a ciclo, así que no era un timestamp congelado, y la
hipótesis de que viniera del simulador no cerraba: el cliente OPC DA de Matrikon,
leyendo el mismo item del mismo servidor, mostraba hora actual.

Eso último era justamente la pista, leída al revés. Durante la Fase 5 se observó
que **con el configurador de MatrikonOPC abierto el desfase desaparece** y los dos
timestamps coinciden al segundo. O sea que el simulador no refresca los timestamps
de sus items cuando el gateway es su único cliente leyendo por `Cache`; basta con
que se conecte cualquier otro cliente DA para que se normalicen. Por eso el
cliente de Matrikon mostraba hora actual: al abrirlo para comparar, se alteraba lo
que se estaba midiendo.

Es una anomalía del simulador y no del gateway. El detalle y cómo reproducirla
están en [operacion.md](operacion.md). Tiene una consecuencia práctica para grabar
material de demo: conviene dejar el configurador **cerrado**, porque el desfase es
exactamente lo que hace visible que `SourceTimestamp` y `ServerTimestamp` son dos
relojes distintos.

## Corrida 2 — 18/08/2026 — Memoria: dos configuraciones en la misma sesión

### Objetivo

Cerrar el asunto abierto de la corrida 1: el mismo escalón de 8.000 tags había
medido 72,7 MB el 14/08 y 148,7 MB durante la Fase 5. Las dos corridas diferían
en dos cosas a la vez (la página de diagnóstico sirviendo y los
`ServerDiagnostics` del stack habilitados) y había una sola medición de cada
lado, así que la diferencia no era atribuible.

### Qué se cambió para poder medirlo

`ServerConfiguration.DiagnosticsEnabled` estaba fijo en `true` en el código.
Se externalizó a `Ua:DiagnosticsEnabled` (default `true`, no cambia el
comportamiento existente). La página web ya tenía su flag en `Web:Enabled`. Con
las dos palancas en configuración, las corridas se distinguen por variables de
entorno y no por editar código entre una y otra.

### Configuración de la corrida

- 8.000 tags (`tags-8000.csv`), simulador con el escenario de carga abierto y el
  configurador cerrado.
- 20 muestras cada 30 s, 10 minutos por corrida, ambas en la misma sesión y en
  la misma máquina sin reiniciar nada en el medio.
- Sin clientes UA conectados: se mide el costo de tener las funciones
  levantadas, no el de consultarlas.
- Corrida A: `Ua:DiagnosticsEnabled=true`, `Web:Enabled=true`.
- Corrida B: ambos en `false`.

### Resultados

| Corrida | Private media | Private min–max | WS media | WS min–max | Handles inicio→fin | Threads inicio→fin |
|---|---|---|---|---|---|---|
| A — todo habilitado | 74,8 MB | 64,8–86,7 | 95,2 MB | 74,3–124,1 | 651 → 617 | 33 → 19 |
| B — todo deshabilitado | 70,6 MB | 59,3–81,7 | 83,6 MB | 62,5–116,1 | 602 → 575 | 29 → 17 |

### Lectura de los resultados

**El costo de los diagnósticos es despreciable.** 4,2 MB de diferencia en Private
Bytes (5,6%), con los rangos casi enteramente solapados: A va de 64,8 a 86,7 y B
de 59,3 a 81,7. Cuando el rango de una medición contiene casi entero al de la
otra, la diferencia entre medias no es señal. La hipótesis de la corrida 1 —que
los `ServerDiagnostics` y la página explicaban un salto de 72,7 a 148,7 MB— queda
descartada.

**La discrepancia era un artefacto de la métrica, no del gateway.** Working Set
es memoria física mapeada y el sistema operativo la recorta cuando quiere; osciló
±25 MB por sí sola en las dos corridas. La forma de la curva lo muestra: al
arranque el Working Set le saca ~43 MB al privado (108 contra 64,8) y al final
convergen (83 contra 81,1). El proceso no estaba liberando memoria — Windows le
estaba sacando páginas prestadas a medida que se asentaba. Private Bytes es
memoria comprometida por el proceso y nadie de afuera la toca, así que es la
métrica que corresponde reportar.

**Los 72,7 MB de la corrida 1 encajan.** Esa medición se hizo con los
diagnósticos deshabilitados, o sea la configuración de la corrida B, cuyo Working
Set fue de 62,5 a 116,1 MB. El número cae dentro del rango.

**Los 148,7 MB de Fase 5 no encajan del todo**, y se anota sin maquillar: quedan
por encima del máximo de Working Set observado hoy en cualquiera de las dos
configuraciones (124,1 MB). La explicación más probable es que se haya medido
temprano, en el pico de asentamiento —donde el Working Set está en su punto más
alto—, o con un cliente UA conectado. No se reprodujo, así que queda como
medición no comparable y no como consumo del gateway.

**Sin fuga de handles ni de threads.** En las dos corridas los handles bajan
(651→617 y 602→575) y los threads también, sin pendiente creciente en ningún
tramo. La diferencia constante de ~45 handles entre A y B es Kestrel, que es
exactamente lo esperable. Es la primera evidencia sobre el riesgo específico de
este proyecto —handles COM que no se liberan—, aunque 10 minutos solo descartan
una fuga grosera.

### Número a citar

**~70-75 MB de Private Bytes para 8.000 tags**, independientemente de si los
diagnósticos están habilitados. No citar Working Set como consumo del gateway.

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
habilitados, configurador de MatrikonOPC **abierto** (ver nota al final),
ventanas de 5 minutos, todo en la misma sesión sin reiniciar el gateway entre
ventanas.

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
diseño de `arquitectura.md`.

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
  parcial que quedó pendiente de la corrida 1.

### Nota sobre el configurador de MatrikonOPC

Estas corridas —y también las de la corrida 2— se hicieron con el **configurador
de MatrikonOPC abierto**. El configurador es un cliente DA más, así que su
presencia normaliza los timestamps del simulador y hace desaparecer el desfasaje
de `SourceTimestamp` documentado en la corrida 1. Para mediciones de recursos y
de tasa de lectura no cambia nada; para grabar material de demo, hay que cerrarlo.


## Corrida 4 — 19/08/2026 — Latencias DA→cache y cache→cliente

### Objetivo

Cerrar el ítem 3 de la Fase 6, obligatorio para el reporte de cierre (§B9): cuánto
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

### Corrección a la nota sobre el configurador de MatrikonOPC

La corrida 3 anotó que el configurador abierto normaliza los timestamps del
simulador. Esta corrida lo pone en duda: la ventana estuvo abierta, con la
configuración cargada, y la barra de estado marcó `Clients: 1` — o sea, el gateway
y nadie más.

La hipótesis corregida es que **lo que normaliza no es tener el configurador
abierto, sino tener un panel que efectivamente lea items** (Quick Client, o un
cliente DA aparte). Si se confirma, no haría falta cerrar nada para grabar la demo.

**Sin verificar todavía:** no se sabe si en las corridas anteriores la barra decía
`Clients: 1` o `Clients: 2`. Se confirma mirando el `SourceTimestamp` en la página
de diagnóstico y viendo si está desfasado ~430 s.

## Corrida 5 — Soak de 2 horas (ítem 4 de la Fase 6)

Última medición de la Fase 6. Objetivo: detectar una fuga lenta de handles COM
que en ventanas de 5-10 minutos no se ve. No requirió código nuevo.

### Configuración de la corrida
- 500 tags (`carga-500.opcsim.xml`), 0 clientes UA, 120 minutos.
- `UpdateIntervalMs` (publicación UA) 1000 · `UpdateRateMs` (lectura DA) 1000.
- Muestreo cada 30 s con `tools/Measure-GatewayMemory.ps1` (240 muestras).
- Configurador de MatrikonOPC abierto sin monitoreo activo (`Clients: 1`).
- Suspensión de Windows desactivada durante la corrida (4 h con corriente alterna).

### Resultados

| Métrica | Base (t+1min) | Final | Mín | Máx |
|---|---|---|---|---|
| Private Bytes | 53,8 MB | 53,4 MB | 51,7 MB | 66,8 MB |
| Handles | 659 | 629 | 546 | 714 |
| Threads | 31 | 18 | 16 | 32 |

Los valores de la primera muestra (66,8 MB / 658 handles / 32 threads) son
transitorio de arranque: JIT, carga de assemblies y armado del address space. La
base real se toma después del primer minuto.

Working Set osciló entre 23,3 y 109,8 MB. **No es una métrica útil acá**: refleja
el recorte de páginas que hace Windows, no el consumo del proceso.

### Lectura de los resultados

**No hay fuga de memoria.** Private Bytes se mantuvo entre 51,9 y 54,1 MB durante
las dos horas, sin pendiente. El valor final está 0,4 MB por debajo de la base.

**Los handles no son planos: hacen diente de sierra.** Suben ~3 cada 30 s y caen
de golpe cada 13-35 minutos:

| Momento | Pico | Caída | Δ |
|---|---|---|---|
| 22:06 | 714 | 638 | −76 |
| 22:19 | 683 | 589 | −94 |
| 22:46 | 696 | 546 | −150 |
| 23:21 | 680 | 554 | −126 |

Es la firma de **RCWs (Runtime Callable Wrappers) liberados por el finalizador y no
de forma determinística**. Cada ciclo DA crea objetos COM envueltos; como no se
llama a `Marshal.ReleaseComObject`, el handle subyacente sobrevive hasta que pasa
el recolector de basura, que corre cuando decide y no cuando termina la lectura.
De ahí la subida pareja y la caída abrupta sin cadencia fija.

**Lo que importa es que el techo no crece.** El pico más alto de las dos horas
(714) ocurrió a los 22 minutos, y el último tramo cerró en 629. Sobre 240 muestras
eso es oscilación acotada, no acumulación.

Threads bajaron de 31 a un régimen estable de 17-19: el ThreadPool de .NET
recortando hilos que no se usan.

### Limitaciones de esta corrida
- **Dos horas, no ocho ni veinticuatro.** El alcance original de la Fase 6 pedía
  soaks más largos. Se recortó deliberadamente (ver README, sección de alcance).
  Una fuga con período mayor a 2 h no se detectaría acá.
- **Sin clientes UA conectados.** Mide el ciclo DA y la cache en régimen, no el
  costo sostenido de sesiones UA. Cada cliente suma handles y threads reales.
- **500 tags, no 8.000.** No se midió si el diente de sierra escala con la cantidad
  de items.
- **Una sola corrida.** Los momentos de caída de handles no son reproducibles a
  demanda; dependen del recolector.

### Deuda que esta corrida confirma
La liberación no determinística de RCWs es aceptable en una PoC, pero en producción
se agregaría liberación explícita (`Marshal.ReleaseComObject` en `finally`) para
bajar el techo de handles y hacerlo predecible. Se documenta como decisión
consciente, no como omisión.

### Actualización a la nota sobre el configurador de MatrikonOPC
La verificación pendiente de la corrida 4 se hizo al inicio de esta corrida: con el
configurador abierto sin monitoreo (`Clients: 1`), el `SourceTimestamp` mostró ~1 s
de diferencia contra `LastUpdateUtc` en los 500 tags, y la antigüedad marcó 0,2 s.
**El desfasaje de ~430 s no se observó.**

Esto **no confirma ni descarta** la hipótesis de Quick Client. El fenómeno ya se
comportó de forma intermitente en corridas anteriores — apareció, desapareció y
volvió a aparecer —, así que una observación puntual no alcanza para concluir nada.

**Estado: anomalía intermitente del simulador, causa no determinada, no
reproducible a demanda.** No afecta a las mediciones ni al diseño: la degradación
por antigüedad se calcula con `LastUpdateUtc` (reloj del gateway), justamente para
no depender de que el timestamp de origen sea confiable.