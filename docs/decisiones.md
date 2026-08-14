# Decisiones de diseño

Registro de decisiones del gateway. **Los números son identificadores estables:
no se renumeran ni se reordenan**, aunque una decisión quede superada. Cuando eso
pasa, la entrada vieja se marca y apunta a la que la reemplaza.

---

## 1. La interfaz de la fuente de datos se ensancha

> **Superada por la decisión 7.** El diagnóstico que sigue se sostiene entero;
> lo que no se sostuvo fue la solución. Se conserva porque el camino desde este
> razonamiento hasta el de la 7 es parte del resultado.

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
  no-bueno. Un tag desconocido y uno con calidad mala serían indistinguibles, y
  en un gateway no pueden serlo.

La conclusión de entonces fue una firma del tipo
`(valor, StatusCode, SourceTimestamp)`. Ver decisión 7 para por qué eso no fue lo
que se implementó.

## 2. El `SourceTimestamp` nunca se pisa

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

## 3. La jerarquía se deriva del nombre del tag

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

## 4. `BaseDataVariableState` antes que `AnalogItemState`

El proyecto anterior usa `AnalogItemState`, que exige `EURange` y
`EngineeringUnits`. El CSV trae `EU` desde la Fase 3, pero todavía no se expone
como propiedad del nodo, y además no existe `AnalogItemState` para `String`.

Se arranca con `BaseDataVariableState`, que acepta cualquier `DataType`, y se
sube a `AnalogItemState` más adelante para los tags que declaren unidad de
ingeniería. Subir es fácil; arrancar con un tipo que no admite uno de los cuatro
tipos de dato no.

## 5. El `PlatformTarget x86` va solo en el host

El driver de OPC DA obliga a un proceso de 32 bits. El `PlatformTarget` manda en
el ejecutable: si el host es x86, todas las bibliotecas que cargue corren en ese
proceso aunque estén compiladas como `AnyCPU`.

Por eso el x86 está en `Gateway.Host` y nada más. Ponerlo en los cinco proyectos
ataría las manos sin beneficio y complicaría una eventual partición en dos
procesos.

**Consecuencia a vigilar:** un proceso de 32 bits tiene un límite práctico de
memoria útil cercano a los 2 GB. No molesta con cientos de tags; puede apretar
en las pruebas de carga con decenas de miles.

## 6. La ubicación de la PKI se fija en el primer arranque

El stack OPC UA genera su propio certificado la primera vez que corre. **Cada
mudanza de la carpeta PKI regenera el certificado y rompe toda la confianza ya
establecida con los clientes.**

Por eso la carpeta se resuelve por ruta absoluta anclada al archivo de solución,
subiendo desde la ubicación del ejecutable — nunca contra el working directory.
Si se resolviera contra el working directory, arrancar con `dotnet run` desde la
raíz y ejecutar el binario desde su carpeta de salida darían dos carpetas PKI
distintas, y el certificado se regeneraría al alternar entre las dos formas.

---

## 7. El contrato se partió en dos, no se ensanchó

La idea original era ensanchar `ITagValueSource` para que devolviera
`(valor, StatusCode, SourceTimestamp)` en vez de solo un valor. Al implementarlo
apareció que con una cache en el medio hay dos preguntas distintas, no una más
grande: el driver DA responde *qué acabo de leer* (`TagSample`) y el node manager
pregunta *cuál es el último estado conocido* (`TagState`). Son ritmos distintos y
no tienen por qué compartir firma.

Lo que se diseñó entonces fue el tipo de dato compartido, no una interfaz común.
`TagSample` viaja del driver a la cache; `TagState` va de la cache al node
manager. El segundo puede devolver un valor viejo, el primero nunca.

Supera a la decisión 1.

## 8. El driver DA no tiene reloj propio

`OpcDaTagSource` expone `Connect()`, `AddItems()`, `ReadAll()` y `Dispose()`, y
nada más. El ciclo que llama a `ReadAll()` vive en el host.

El desacople de ritmos es trabajo de la cache y del host; si además el driver
tuviera su propio timer habría dos relojes y ninguno dueño. Un driver sin hilos
adentro también es mucho más simple de razonar cuando haya que agregar
reconexión (Fase 4).

La contrapartida es que la restricción de apartment COM (MTA) se le escapa al
driver y pasa al host. Se resuelve validando: `Connect()` verifica el apartment
y falla con un mensaje legible si no es MTA.

## 9. El hilo de adquisición DA es explícito y separado

El ciclo DA corre en un `Thread` propio, creado con
`SetApartmentState(ApartmentState.MTA)`, y no en el timer de publicación UA.

Dos razones. Primera: hasta acá el MTA se obtenía de rebote, porque un `Main`
async corre sobre el thread pool, que es MTA por defecto — funcionaba, pero por
accidente. Segunda: una lectura DA lenta no tiene por qué frenar la publicación
UA, y compartir hilo garantizaría lo contrario.

## 10. Ante una lectura mala se conserva el último valor

Cuando llega una muestra con calidad no utilizable, la cache mantiene el valor
anterior **y su `SourceTimestamp` original**, y le pega la calidad nueva. No se
descarta el valor ni se refresca su hora.

El criterio viene de cómo se usa el dato aguas abajo: un operador que ve un campo
vacío no puede distinguir un tag caído de un tag que nunca existió, mientras que
un valor con indicación de calidad mala le dice cuánto valía la medición hasta
hace un rato y que dejó de actualizarse. La propia especificación de OPC DA
reserva un subestado para esto (`BadLastKnown`), lo que indica que conservar era
el comportamiento asumido.

Lo que nunca se hace es conservar el valor y refrescarle el timestamp: ahí
desaparece toda evidencia de que está congelado.

El caso apareció en la primera corrida real. Leyendo con `Cache`, el servidor DA
devolvió los valores de la sesión anterior marcados `BadOutOfService`, con
timestamps de doce minutos antes de que el gateway arrancara. Sin la guarda de
`IsUsable`, esos números se habrían escalado y publicado con apariencia de dato
fresco.

## 11. Un item DA puede alimentar varios nodos UA

La relación entre `TAG_NAME_OPC_DA` y `TAG_NAME_OPC_UA` es uno a muchos: el mismo
punto del servidor legado puede exponerse más de una vez con transformaciones
distintas — la misma presión en bar y en kg/cm², por ejemplo.

Se descubrió porque la primera versión de la cache indexaba las definiciones por
nombre DA en un diccionario y el gateway no arrancaba con un CSV que repetía un
ItemID. La corrección fue agrupar: por cada nombre DA, la lista de definiciones
que lo consumen.

## 12. La traducción de calidad va con `switch` explícito

Los enums de calidad del SDK (`OpcDaQualityMaster`, `OpcDaQualityStatus`,
`OpcDaQualityLimit`) coinciden en nombre y valor numérico con los de
`Gateway.Core`, así que un cast habría funcionado. Se usa `switch` de todas
formas.

Un cast sigue compilando si el SDK renumera un enum en una versión futura, y
falla en silencio sobre el dato: el gateway publicaría calidades equivocadas sin
que nada avise. El `switch` falla ruidoso. El costo son quince líneas.

Los casos por defecto caen siempre en el lado conservador (`Bad`, `NotLimited`):
ante un valor desconocido, el gateway no puede afirmar que el dato está bien.

La tabla de correspondencia completa está en [calidad-da-ua.md](calidad-da-ua.md).

## 13. Se lee con `Cache`, no con `Device`

`OpcDaGroup.Read` acepta dos orígenes. `Device` fuerza una lectura contra el
dispositivo; `Cache` devuelve lo que el servidor DA ya tiene en su propia cache,
refrescada a su ritmo.

Se eligió `Cache` porque es el único modo que escala: con `Device`, el tráfico al
dispositivo crece con la cantidad de lecturas del gateway, y evitar exactamente
eso es la razón de ser de la cache propia
(ver [arquitectura.md](arquitectura.md#por-qué-la-cache-es-el-centro)). Un
gateway que le pega al equipo legado en cada ciclo traslada al dispositivo la
carga que debería absorber él.

El precio es latencia, acotada por el `UpdateRate` del grupo. Medido contra
Matrikon con `UpdateRate` de 1000 ms: el timestamp que devuelve `Cache` llega
~124 ms antes del instante de lectura, contra ~1 ms con `Device`. Frente al
intervalo de publicación UA (1000 ms), es despreciable.

Dos aclaraciones para no prometer de más:

- Matrikon estampa toda la tanda con el mismo instante en ambos modos, así que
  el timestamp por item que `Cache` podría dar en teoría acá no aparece. Otro
  servidor DA podría comportarse distinto.
- Una versión anterior de este archivo justificaba `Device` afirmando que
  `Cache` desincronizaba valor y timestamp por varios minutos. Medido después,
  es falso: el desfase se observaba idéntico en los dos modos, así que no era
  atribuible al origen de lectura. Ver la anomalía en
  [operacion.md](operacion.md#sourcetimestamp-atrasado-7-minutos-fase-2).