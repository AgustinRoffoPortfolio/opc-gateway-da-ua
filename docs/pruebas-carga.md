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

**Nota sobre la configuración.** Los números de la corrida 1 se midieron con los
diagnósticos del servidor **deshabilitados** (el default del stack). Se
habilitaron después, para esta verificación, y quedaron habilitados en el
código. Mantener contadores por sesión y por suscripción tiene costo, así que
una corrida futura no es directamente comparable contra esta tabla.

Ya hay evidencia de que la diferencia no es despreciable: una corrida posterior
del mismo escalón de 8.000, durante la Fase 5 y con la página de diagnóstico
sirviendo además de los `ServerDiagnostics` habilitados, midió **148,7 MB** contra
los 72,7 MB de esta tabla. No alcanza para atribuirle la diferencia a una causa
—son dos cambios a la vez y una sola medición—, pero sí para no citar los 72,7 MB
como el consumo del gateway tal como está hoy. La corrida 2 debería medir las dos
configuraciones en la misma sesión.

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