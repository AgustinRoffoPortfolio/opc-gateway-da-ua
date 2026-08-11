# Traducción de calidad: OPC DA → OPC UA

> **Estado:** diseñado, no implementado. La tabla se implementa en la Fase 2,
> junto con la lectura real del servidor DA. Los valores numéricos de los códigos
> DA se verifican contra el SDK antes de darlos por buenos.

## Por qué hace falta traducir

Los dos estándares dicen lo mismo con vocabularios distintos e incompatibles.
Un valor no viaja solo: viaja con una afirmación sobre cuánto se puede confiar en
él. Si esa afirmación se pierde en el camino, el cliente recibe un número que
parece válido y no lo es.

**Esta es la parte conceptualmente difícil del proyecto.** El resto es plomería.

## Cómo es la calidad del lado DA

No es un booleano ni un enum plano: es un campo de bits que combina tres cosas.

```
 bits 7-6 │ bits 5-2  │ bits 1-0
  Quality │ Substatus │  Limit
```

- **Quality** — el nivel general: `Bad`, `Uncertain` o `Good`.
- **Substatus** — la causa concreta dentro de ese nivel. No es lo mismo un `Bad`
  porque se cayó la comunicación que un `Bad` porque el instrumento está fuera de
  servicio: el primero se resuelve solo, el segundo requiere que alguien vaya.
- **Limit** — si el valor está pegado a un límite (alto, bajo o constante).

La consecuencia práctica: aplanar la calidad DA a "sirve / no sirve" tira a la
basura la información que hace útil un diagnóstico. Es la diferencia con Modbus,
donde la calidad efectivamente se reduce a si el dispositivo contestó.

## Cómo es del lado UA

UA tiene `StatusCode`, un código de 32 bits con su propio catálogo de nombres
(`Good`, `Uncertain`, `BadCommunicationError`, `BadOutOfService`, y muchos más).
Cubre aproximadamente las mismas situaciones, pero los nombres no coinciden y la
correspondencia no es uno a uno.

## La tabla

Va en una **tabla explícita**, en un solo lugar, no repartida en condicionales por
el código. Es la regla de negocio central del gateway y tiene que poder leerse de
un vistazo, auditarse y modificarse sin tocar la lógica de adquisición.

| Calidad OPC DA | StatusCode OPC UA | Comentario |
|---|---|---|
| `Good` | `Good` | Caso normal. |
| `Uncertain` | `Uncertain` | El valor sirve, pero con reservas. |
| `Bad` | `Bad` | Genérico, sin causa identificada. |
| `Bad_NotConnected` | `BadCommunicationError` | El item no está conectado a una fuente de datos. |
| `Bad_OutOfService` | `BadOutOfService` | El item está deshabilitado en el servidor DA. |
| `Bad_LastKnownValue` | `UncertainLastUsableValue` | Ver abajo: es la única fila que cambia de nivel. |

Todo código DA que no esté en la tabla cae en `Bad` genérico. Mejor un
conservador de más que un `Good` inventado.

## La fila que cambia de nivel

`Bad_LastKnownValue` es `Bad` en DA y se traduce a `Uncertain` en UA. Es la única
traducción que no conserva el nivel, y es deliberada.

El caso es este: el servidor DA perdió contacto con el dispositivo y está
entregando el último valor que conoció. DA lo clasifica como `Bad` porque no es
una lectura fresca. UA tiene un código más preciso —`UncertainLastUsableValue`—
que dice exactamente eso: el dato es viejo pero utilizable.

Traducirlo a `Bad` a secas sería técnicamente fiel y prácticamente peor: el
cliente perdería la distinción entre "hay un valor viejo acá" y "no hay nada".
Cuando el estándar de destino puede expresar algo con más precisión que el de
origen, se usa la precisión.

## Qué pasa con la transformación de unidades

La transformación es `Valor_UA = Valor_DA * MULTIPLICADOR + OFFSET`.

**Solo se aplica si el valor es numérico y la calidad es buena o utilizable.** Si
la calidad es mala, o el valor no convierte al tipo declarado, el nodo publica un
`StatusCode` no-bueno y no se publica un valor escalado.

El motivo: un valor escalado sobre una lectura mala es peor que no publicar nada,
porque **parece válido**. Pasó por una fórmula, tiene la magnitud correcta y las
unidades correctas. Nada en el número delata que la lectura de la que salió no
servía.

## Fuera de alcance de la PoC

- **Los bits de límite.** UA también sabe expresar que un valor está en su límite
  alto o bajo, así que la traducción es posible. Queda afuera porque no aporta a
  lo que la PoC quiere demostrar, no porque no se pueda.
- **Los subcódigos de `Uncertain`** (sensor impreciso, fuera del rango de
  ingeniería, sub-normal). Todos caen hoy en `Uncertain` genérico.

Ambas son extensiones de la tabla, no cambios de diseño: agregar filas, no
reescribir la traducción.