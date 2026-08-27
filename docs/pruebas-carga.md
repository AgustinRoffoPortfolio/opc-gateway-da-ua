# Pruebas de carga — escala, memoria y soak

Mediciones de volumen del gateway: cuántos tags sostiene, cuánta memoria consume y
si pierde recursos con el tiempo.

Las corridas que miden **rendimiento** —el aislamiento que da la cache y las
latencias de punta a punta— están en
[`pruebas-carga-rendimiento.md`](pruebas-carga-rendimiento.md). La numeración de
corridas es global a los dos documentos y es cronológica, así que acá aparecen la
1, la 2 y la 5, y allá la 3 y la 4. Los números no se reordenan: otros documentos
las citan así.

## Números a citar

| Qué | Valor | Corrida |
|---|---|---|
| Tags sostenidos de punta a punta | 8.000, sin errores, arranque < 1 s | 1 |
| Memoria con 8.000 tags | ~70-75 MB de Private Bytes | 2 |
| Memoria con 500 tags | 51,9-54,1 MB de Private Bytes | 5 |
| Fuga en 2 h de soak | ninguna, sin pendiente | 5 |

**Siempre Private Bytes, nunca Working Set.** Working Set es memoria física mapeada
y Windows la recorta por su cuenta: osciló ±25 MB sin que el gateway hiciera nada
(corrida 2). Private Bytes es memoria comprometida por el proceso y nadie de afuera
la toca.

Dos aclaraciones que hay que hacer al citar estos números: los **8.000 aliases se
alimentan de 4 ItemID de origen** —es el peor caso para el lado UA, pero no son
8.000 señales independientes—, y la **CPU es presupuesto compartido** con el
servidor DA, que corre en la misma máquina.

## Nota transversal — el desfasaje de `SourceTimestamp`

Todas las corridas de este documento y del de rendimiento se hicieron mientras el
`SourceTimestamp` llegaba a UaExpert desfasado ~7 minutos de forma intermitente, y
mientras ese desfasaje se atribuía —incorrectamente— al simulador de Matrikon. La
causa real se identificó el 21/08/2026: un error de conversión de `FILETIME` en el
SDK cliente DA. La aritmética, la evidencia y la corrección están en
[`bug-filetime-sdk.md`](bug-filetime-sdk.md) y no se repiten acá.

**Qué implica para estos números: nada.** Ninguna medición usa `SourceTimestamp`
como instrumento. El tramo DA→cache se cronometra con `Stopwatch` dentro del ciclo
de adquisición, el tramo cache→cliente usa un nodo sonda contra el reloj de la
máquina, y la degradación por antigüedad se calcula con `LastUpdateUtc`. La
condición "configurador de MatrikonOPC abierto" que figura en las corridas 2, 3 y 4
se eligió por una hipótesis falsa, pero no tuvo efecto sobre ningún resultado
publicado: los instrumentos ya eran independientes del timestamp de origen.

Queda anotado porque el error de método es material de entrevista: la hipótesis se
sostenía en una única observación —al abrir el explorador de Matrikon para
comparar, el desfase desaparecía— leída como "el observador altera lo que mide".
Los datos para descartarla ya estaban en la corrida 1: el desfase alternaba entre
ciclos consecutivos y siempre valía el mismo número. Lo que faltó no fue mirar más,
fue medir y reconocer el número.

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
huérfano adelante. Que los dos nombres difieran es precisamente para lo que existe
la columna de mapeo del CSV.

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

| Escalón | Tags válidos | Errores | Arranque address space | Memoria proceso (WS) | CPU proceso | Tags suscriptos UA |
|---|---|---|---|---|---|---|
| 500 | 500 | 0 | < 1 s | no medido | — | 1 |
| 4.000 | 4.000 | 0 | < 1 s | 73,7 MB | 0 % | 40 |
| 8.000 | 8.000 | 0 | < 1 s | 72,7 MB | 0,4 % | 800 |

Soak de 33 minutos en el escalón de 8.000, con los 800 tags suscriptos activos
durante toda la corrida:

| Momento | Memoria `Gateway.Host` (WS) | Memoria COM Surrogate (WS) |
|---|---|---|
| Inicio | 72,7 MB | 0,6 MB |
| +33 min | 73,2 MB | 1,0 MB |

Estos números son Working Set y se midieron con los diagnósticos del servidor
deshabilitados. La corrida 2 los revisa con la métrica correcta y confirma que
caen dentro del rango esperado.

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

El escalón de 8.000 destapó además una anomalía del stack UA de la OPC Foundation
(`Oops! MonitoredItems queued but no notifications available`), que se investigó y
se midió por su efecto en el cliente. Está en
[`verificacion.md`](verificacion.md), sección de Fase 6: es un hallazgo de
verificación, no una medición de volumen.

### Limitaciones de esta corrida

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
  `Bucket Brigade`) quedó **fuera del alcance** de la Fase 6, recortado
  deliberadamente.
- **Un solo cliente UA.** Se midió después: ver la corrida 3 en
  [`pruebas-carga-rendimiento.md`](pruebas-carga-rendimiento.md).
- **Soak corto.** 33 minutos detectan una fuga grosera. La corrida 5 lo extiende a
  2 h; los soaks de 8 y 24 h del roadmap original se recortaron.
- **Sin latencia medida.** Se midió después: ver la corrida 4 en
  [`pruebas-carga-rendimiento.md`](pruebas-carga-rendimiento.md).
- **CPU compartida.** El gateway comparte máquina con el servidor DA. Los
  números de CPU son de presupuesto compartido, no propio.
- **Escalón de 500 sin memoria medida.**

## Corrida 2 — 18/08/2026 — Memoria: dos configuraciones en la misma sesión

### Objetivo

Cerrar una discrepancia de la corrida 1: el mismo escalón de 8.000 tags había
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
por encima del máximo de Working Set observado en cualquiera de las dos
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

## Corrida 5 — Soak de 2 horas

Última medición de la Fase 6. Objetivo: detectar una fuga lenta de handles COM
que en ventanas de 5-10 minutos no se ve. No requirió código nuevo.

### Configuración de la corrida

- 500 tags (`carga-500.opcsim.xml`), 0 clientes UA, 120 minutos.
- `UpdateIntervalMs` (publicación UA) 1000 · `UpdateRateMs` (lectura DA) 1000.
- Muestreo cada 30 s con `tools/Measure-GatewayMemory.ps1` (240 muestras).
- Configurador de MatrikonOPC abierto sin monitoreo activo (`Clients: 1`).
- Suspensión de Windows desactivada durante la corrida.

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