# El bug de FILETIME del SDK cliente DA

Durante las fases 2 a 5, el `SourceTimestamp` de algunos tags llegaba con
**7 minutos y 9,5 segundos de atraso**, de forma intermitente: aparecía y
desaparecía solo, sin patrón evidente. Este documento cuenta qué era, cómo se
encontró, y cómo se corrigió.

## Qué es

Un `FILETIME` de Windows son 64 bits de ticks de 100 ns, partidos en dos campos
de 32. En `System.Runtime.InteropServices.ComTypes.FILETIME` los dos campos están
declarados como **`int` con signo**. El SDK los recomponía así:

```csharp
var lft = (((long)fileTime.dwHighDateTime) << 32) + fileTime.dwLowDateTime;
```

Cuando el bit 31 del campo bajo está prendido, ese `int` se extiende con signo a
un valor negativo, y el resultado sale exactamente **2³² ticks abajo**:

2^32 = 4.294.967.296 ticks × 100 ns = 429,4967296 s = 7 min 9,5 s


El bit 31 alterna cada **214,7483648 s** (~3 min 35 s), así que el error aparece
y desaparece en bloques de ese largo. De ahí el "a veces coincide y a veces no".

### Cómo reconocerlo en cualquier log

Si los timestamps correctos caen en segundos redondos (`.0000000`), los corruptos
terminan siempre en **`.5032704`**, porque `10.000.000 − 4.967.296 = 5.032.704`.
Ese patrón de dígitos no lo produce ninguna otra causa.

## Cómo se corrigió

Upstream arregló el bug en el commit `19ab01b` (agosto de 2021) agregando la
máscara que faltaba:

```csharp
var lft = (((long)fileTime.dwHighDateTime) << 32) + (fileTime.dwLowDateTime & 0xFFFFFFFF);
```

**Ese arreglo nunca se publicó en NuGet.** Ninguno de los tres paquetes
disponibles lo tiene, incluido el que usa este proyecto. Como la línea está
compilada dentro del binario, el gateway no puede evitar la resta: la **revierte**
en `Gateway.Da.SdkTimestamp.Correct()`, que se llama en un único punto, al
construir el `TagSample`.

La corrección es exacta, no heurística. Restar 2³² solo toca los bits ≥ 32, así
que los 32 bits bajos del valor corrupto son idénticos a los del valor real: el
bit 31 prendido a la salida del SDK identifica de forma biunívoca al valor
corrupto. No hay umbrales ni depende de la antigüedad del dato.

**No es idempotente**: el valor ya corregido conserva el bit 31 prendido, así que
una segunda pasada sumaría otros 7 minutos.

## Evidencia

### Test de caracterización

`tests/Gateway.Tests/SdkFileTimeConverterTests.cs` construye dos `FILETIME`
separados por 1 tick a distinto lado del bit 31 y verifica que el SDK los devuelve
separados por 2³²−1 ticks. No usa COM ni servidor DA: es aritmética pura, llamada
por reflection porque `FileTimeConverter` es `internal`.

Ese test afirma que **el bug está presente**. Si algún día falla, no se rompió
nada: significa que el binario del SDK quedó arreglado y que la corrección se
puede sacar.

`tests/Gateway.Tests/SdkTimestampTests.cs` prueba la corrección en sí, incluido
el caso de un dato legítimamente viejo que no debe tocarse.

### Medición contra el simulador

`tools/TimestampProbe` registra por cada lectura el timestamp crudo del SDK y el
corregido en la misma fila, así que una sola corrida da los dos escenarios sobre
exactamente los mismos datos. Corrida del 21/08/2026, 15 minutos, 10 tags, 892
ciclos — `docs/evidencia/timestamp-probe-15min.csv`:

| Delta | Lecturas |
|---|---|
| 429,4967296 s | 5.671 |
| 0 s | 3.249 |

**Solo existen esos dos valores.** Ni uno intermedio en 8.920 lecturas: un atraso
real produciría un continuo, no dos valores exactos. Es un bit, no un fenómeno
temporal.

| Tag | Lecturas | Corregidas |
|---|---|---|
| Random.Real8 / Real4 / Int4 / UInt4 | 892 c/u | 428 |
| Random.Int2 | 892 | 427 |
| Saw-toothed Waves.Real8 | 892 | 428 |
| Triangle Waves.Real8 | 892 | 428 |
| Bucket Brigade.Real8 / Int4 / Real4 | 892 c/u | 892 |

Dos lecturas de esta tabla:

- Los siete tags que avanzan se corrigen **los mismos ciclos**, no cada uno por
  su lado. Si la causa fuera del servidor DA, cada item tendría su propia
  historia; que alternen sincronizados prueba que el punto común es el conversor.
  428/892 = 47,98% contra el 50% teórico, y la diferencia se explica sola: en 892 s
  entran ~4,15 bloques de 214,75 s, y el último queda cortado.
- Los tres `Bucket Brigade` son tags estáticos, congelados tres días atrás. Se
  corrigen en el 100% de las lecturas y **se corrigen bien**: la regla mira el bit,
  no la antigüedad. Es la versión empírica del tercer caso del test.

### Upstream y terceros

- Issue [#12](https://github.com/titanium-as/TitaniumAS.Opc.Client/issues/12) —
  abierto, contra un servidor OPC real. El arreglo exacto está publicado ahí desde
  diciembre de 2017.
- Issue [#16](https://github.com/titanium-as/TitaniumAS.Opc.Client/issues/16) —
  cerrado, contra el simulador de Matrikon, con log mostrando 7 min 10 s exactos.
  Se sugirió cambiar el modo de lectura; el autor confirmó que no servía.
- Un tercero, con AVEVA System Platform sobre Ethernet/IP de Allen-Bradley,
  midió el mismo desfasaje con este SDK. Tres servidores DA distintos, un solo
  denominador común.

## El error de método, que es la parte que más enseña

Antes de esto, el desfase estaba documentado como una anomalía del simulador de
Matrikon. La evidencia que lo había convencido: *"al conectar MatrikonOPC Explorer
el atraso cae a ~0,84 s en el ciclo exacto de la conexión"*.

Se observó **una sola vez**. Bajo el mecanismo real, fue una coincidencia con un
borde de bloque de ~3 min 35 s. Una observación única, con una explicación causal
plausible encima, y ninguna réplica. La hipótesis sobrevivió tres fases.

Lo que la derribó no fue mirar más: fue medir. El desfase era exactamente
429,4967296 s, siempre el mismo número, y ese número es 2³² ticks — la firma
aritmética de un error de signo, no de un servidor lento.

## Qué NO hay que volver a investigar

- **El simulador de Matrikon no tiene la culpa** de este desfase (sí tiene otra
  anomalía distinta, la de los aliases: ver `operacion.md`).
- **Cambiar el modo de lectura no sirve.** `Device` vs `Cache`, `ReadAsync` vs
  `RefreshAsync` vs suscripciones: todos pasan por el mismo `FileTimeConverter`.
  Medido acá y confirmado en el issue #16.
- **No hay ningún paquete de NuGet con el fix.** Verificado el 21/08/2026.
- La degradación por antigüedad se sigue midiendo con `LastUpdateUtc`, nunca con
  `SourceTimestamp`.