# Configuración de tags — el CSV

El gateway no tiene tags cableados en el código: todo lo que expone sale de un
archivo CSV que se lee una vez al arrancar. Este documento describe su formato,
qué pasa cuando una fila está mal, y las decisiones de diseño detrás de un par
de campos que no son obvios.

La ruta del archivo sale de `Ua:TagsCsvPath` en `appsettings.json`. Al repo va
únicamente `config/tags.example.csv`, con nombres inventados.

## Formato

Separador `;`, decimales con **punto**, comentarios con `#` al inicio de línea.
La primera línea no comentada es la cabecera y se saltea.

TAG_NAME_OPC_UA;TAG_NAME_OPC_DA;DATA_TYPE;MULTIPLICADOR;OFFSET;EU;SCAN_RATE_MS;DEADBAND;ACCESS_LEVEL;DESCRIPTION;ENABLED


| # | Campo | Qué es | Estado |
|---|---|---|---|
| 1 | `TAG_NAME_OPC_UA` | Nombre expuesto al cliente UA. Los puntos derivan la jerarquía de carpetas. Único en el archivo. | En uso |
| 2 | `TAG_NAME_OPC_DA` | ItemID en el servidor DA. Puede repetirse: el mismo punto DA se expone más de una vez con transformaciones distintas. | En uso |
| 3 | `DATA_TYPE` | `Double`, `Boolean`, `Int32` o `String`. | En uso |
| 4 | `MULTIPLICADOR` | Factor de escala. `Valor_UA = Valor_DA * MULTIPLICADOR + OFFSET`. | En uso |
| 5 | `OFFSET` | Desplazamiento de la misma fórmula. | En uso |
| 6 | `EU` | Unidad de ingeniería (bar, °C, m3). | **Solo viaja** |
| 7 | `SCAN_RATE_MS` | Frecuencia de lectura deseada para ese tag. | **Solo viaja** |
| 8 | `DEADBAND` | Umbral de cambio mínimo para considerar que el valor cambió. | **Solo viaja** |
| 9 | `ACCESS_LEVEL` | `Read` o `Hidden`. Ver más abajo. | En uso |
| 10 | `DESCRIPTION` | Texto libre para el operador. | **Solo viaja** |
| 11 | `ENABLED` | `True` / `False`. | En uso |

**"Solo viaja"** significa que el campo se parsea, se valida y llega al objeto
`TagDefinition`, pero ningún componente lo consume todavía. Se declaran ahora
para no tener que migrar el formato del CSV en cada fase: `EU` y `DEADBAND` los
usará la Fase 5, `SCAN_RATE_MS` la Fase 6 cuando haya varios grupos DA con
frecuencias distintas. Está anotado a propósito para que nadie asuma que un
tag con `DEADBAND=0.5` está filtrando algo.

## Carga parcial: un tag malo no tira el gateway

La política es explícita: **una fila inválida queda fuera de servicio, el resto
del archivo se sirve igual.** El gateway arranca, loguea cada error como
`Warning`, informa el conteo, y expone los tags que sí cargaron.

La alternativa —abortar el arranque ante el primer error— parece más segura pero
es peor en operación. Un CSV de producción con cientos de tags va a tener alguna
fila mal tarde o temprano, y un gateway que no levanta deja ciegos a *todos* los
clientes UA por culpa de un tag. Fallar parcialmente y decir con precisión qué
falló es más útil que fallar entero.

El costo de esta decisión es que el operador tiene que leer el log: un tag
faltante no se anuncia solo del lado UA, simplemente no está. Por eso los
mensajes de error incluyen archivo, número de línea, nombre del tag y qué se
esperaba.

### Cómo se reparte la validación

Hay dos niveles, y la división no es arbitraria:

- **`CsvTagLoader`** (`internal`) valida cada fila **de forma aislada**: cantidad
  de columnas, tipos que parseen, enums válidos. No compara filas entre sí.
- **`TagValidator`** (público, el único punto de entrada) llama al loader y
  encima aplica las reglas que **necesitan ver el archivo completo**. Hoy la
  única es la unicidad de `TAG_NAME_OPC_UA`; ante un duplicado gana la primera
  aparición, por ser el criterio más fácil de explicar.

Cualquier código que necesite leer el CSV pasa por `TagValidator.LoadAndValidate`,
nunca por el loader directo.

### Salida real ante un CSV con cinco errores distintos

[WRN] ... linea 5: tiene 10 columnas, se esperaban 11 (...).
[WRN] ... linea 6, tag 'PLANTA_02.ERROR.TIPO_INVALIDO': DATA_TYPE 'Float64' no es valido (valores aceptados: Double, Boolean, Int32, String).
[WRN] ... linea 7, tag 'PLANTA_02.ERROR.MULTIPLICADOR': MULTIPLICADOR '1,5' no es un decimal valido (se espera PUNTO como separador decimal, no coma).
[WRN] ... linea 8, tag 'PLANTA_02.ERROR.ACCESS_LEVEL': ACCESS_LEVEL 'ReadWrite' no es valido (valores aceptados: Read, Hidden).
[WRN] ... linea 9: 'PLANTA_01.MEDICION.PRESION_ENTRADA' ya aparecio antes en el archivo, esta fila queda fuera de servicio.
[INF] Tags cargados: 3 validos, 5 con error
[INF] Address space listo: 3 tags


Los mensajes listan los valores aceptados a propósito: el texto que da
`Enum.Parse` por defecto (*"Requested value 'Float64' was not found"*) obliga a
ir a leer el código para corregir una fila de configuración.

## Decisiones de diseño

### `ACCESS_LEVEL` no habilita escritura

El campo tiene exactamente dos valores, `Read` y `Hidden`, y **ninguno de los
dos toca la escritura**. El gateway es de solo lectura hasta la Fase 8, y este
campo no es la puerta por la que eso cambia.

El nombre `ACCESS_LEVEL` invita a que alguien agregue un `Write` "que falta",
y en un gateway contra un servidor legado en producción una escritura accidental
no es un bug de software: es una válvula que se mueve. La habilitación de
escritura, si llega, va a ser una decisión explícita con su propio diseño, no
un valor nuevo en un enum de configuración.

`Read` es el default y significa lo esperable: el tag se publica como nodo UA
con `AccessLevel = CurrentRead`.

### `Hidden` no es lo mismo que `ENABLED=False`

Son dos formas distintas de que un tag no aparezca, y la diferencia importa:

| | Se lee del DA | Está en la cache | Se publica como nodo UA |
|---|---|---|---|
| `ACCESS_LEVEL=Hidden` | Sí | Sí | **No** |
| `ENABLED=False` | No | No | No |

`Hidden` sirve para un tag que el gateway necesita internamente —o que se quiere
tener a mano para diagnóstico— pero que no debe verse desde afuera. `ENABLED=False`
es dar de baja el tag entero.

El filtro vive en `GatewayNodeManager.CreateAddressSpace`, que saltea los
`Hidden` al construir el árbol. Como la carpeta intermedia solo se crea cuando
se agrega un tag que la necesita, un `Hidden` que era el único hijo de su rama
no deja carpeta vacía: la rama directamente no existe.

Verificado en UaExpert: con un tag marcado `Hidden`, el log reporta 10 tags
cargados y 9 en el address space, y el árbol del cliente muestra 9.

### Cultura invariante no alcanza para rechazar la coma decimal

El caso más caro que apareció en esta fase, y no lo detectaron los tests.

El CSV se parsea con `CultureInfo.InvariantCulture`, lo cual garantiza que el
punto se lea como separador decimal. Lo que **no** garantiza es que la coma se
rechace: el `NumberStyles` por defecto de `double.Parse` incluye
`AllowThousands`, y en cultura invariante la coma es un separador de miles
perfectamente válido.

Consecuencia: un `MULTIPLICADOR` de `1,5` —el resultado típico de abrir el CSV
con Excel en configuración regional es-AR y guardarlo— parseaba **en silencio
como 15**. El tag cargaba sin errores, el gateway arrancaba normal, y el valor
publicado salía escalado diez veces mal. Un error de configuración que no hace
ruido es peor que uno que rompe el arranque.

La corrección es pasar `NumberStyles.Float` explícito (sin `AllowThousands`) en
`MULTIPLICADOR`, `OFFSET` y `DEADBAND`. El mismo razonamiento aplica a los enums:
`Enum.TryParse` acepta enteros sueltos, así que un `DATA_TYPE` numérico pasaría
como enum inválido — de ahí el `Enum.IsDefined` adicional.

Los tests unitarios no lo encontraron porque probaban el multiplicador con
`abc`, que falla con y sin el bug. Apareció al correr el gateway completo contra
un CSV con errores deliberados, que es exactamente para lo que sirve verificar
con los propios ojos y no solo con la suite en verde.