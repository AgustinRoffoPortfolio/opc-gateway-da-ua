# Operación

Cómo se levanta el gateway, qué mirar cuando algo falla, y las anomalías
observadas contra sistemas reales.

> Documento en construcción. El procedimiento de arranque y el rollback se
> completan en la Fase 7. Por ahora acumula hallazgos de campo.

## Anomalías observadas

### `SourceTimestamp` atrasado (Fase 2, confirmado en Fase 3)

Durante la verificación de la Fase 2, los valores llegaban correctos a UaExpert
pero con un `SourceTimestamp` ~7 minutos anterior a la hora real. Se observó dos
veces, con magnitud casi idéntica (7m09,5s y 7m10s).

**Qué se descartó.** El desfase era constante entre lecturas sucesivas
(2,047000 s de avance del reloj del servidor contra 2,046793 s de reloj real), lo
que descarta drift. Se instrumentó el borde del driver logueando el
`DateTimeOffset` crudo del SDK, antes de cualquier conversión, junto al reloj de
pared tomado en la misma línea: los dos venían corridos por la misma cantidad, así
que el gateway transmitía fielmente lo que el servidor DA le daba. Eso descartó
el driver, la conversión a UTC y el modo de lectura en una sola corrida. También
se descartaron cambios de hora del sistema (sin eventos `Kernel-General` Id 1 en
la ventana), suspensión, reinicio del simulador (mismo PID) y configuración de
alias: en la segunda ocurrencia, un alias y el item directo que lo alimenta
mostraron el mismo timestamp en la misma ventana de MatrikonOPC Explorer.

**El patrón, confirmado en Fase 3.** Las dos veces el desfase apareció con el 
simulador sin clientes DA activos fuera del gateway, y las dos veces desapareció
al agregar un item en MatrikonOPC Explorer. En la segunda ocurrencia Explorer
mostraba la hora correcta mientras el gateway recibía la atrasada. La hipótesis
de trabajo es que el simulador no refresca los timestamps de sus items sin
lectura activa de por medio, y que el gateway solo no alcanza para mantenerlo
despierto. En la Fase 2 no se confirmó ni se persiguió más — el driver ya estaba
descartado por medición y el objetivo de la fase no era diagnosticar el
simulador — y quedó como deuda, que es lo que cierra el bloque siguiente.

**Confirmación (Fase 3).** Una tercera corrida, instrumentada igual, cerró la
hipótesis con tres evidencias que en la Fase 2 no se tenían. Primera: el desfase
no es un estado que se instala, sino que **alterna entre ciclos consecutivos** —
lecturas separadas por un segundo daban 0,865 s y 430,341 s de atraso — lo que
descarta cualquier reloj que derive o se resincronice. Segunda: los timestamps
atrasados terminan **siempre** en la misma fracción de sub-milisegundo (`...2704`)
y los frescos en `...0000000`, o sea que el servidor responde desde dos bases de
tiempo distintas, no desde una que se corrige. Tercera: matando el proceso
`OPCSim` y dejando que COM lo relevante de nuevo, el desfase reaparece con la
misma firma
y retomando la hora donde la había dejado la instancia anterior, así que el
offset no vive en la memoria del proceso.

El cierre llegó al conectar MatrikonOPC Explorer sobre el mismo `ItemID` mientras
el gateway corría: el atraso pasó de ~430 s a ~0,84 s **en el ciclo exacto** en
que Explorer agregó el item, y la firma de sub-milisegundos cambió con él.
Confirma la hipótesis de Fase 2: el simulador no refresca los timestamps de sus
items sin un cliente que lo mantenga despierto, y una lectura por `Cache` desde
el gateway solo no alcanza. El reporte original de que "Explorer mostraba hora
correcta" se explica solo: la mostraba **porque su propia presencia la producía**.

**Resolución: no se corrige, se documenta.** Es comportamiento del simulador, no
del gateway. La evidencia medida en el borde del driver muestra que el
`SourceTimestamp` se transporta fiel; corregirlo del lado del gateway implicaría
pisarlo con la hora local, que es exactamente lo que la decisión 2 prohíbe.
Contra un servidor DA de producción hay que volver a verificarlo antes de dar por
sentado que no ocurre.

**Consecuencia para la Fase 4.** La degradación por antigüedad se mide con
`LastUpdateUtc` (reloj del gateway, cuándo refrescamos nosotros) y no con
`SourceTimestamp`. Con esta anomalía activa, un criterio basado en
`SourceTimestamp` marcaría como caídos tags que están llegando bien, y el
objetivo de detección < 10 s sería inalcanzable por una causa ajena al gateway.

**Estado verificado.** Con el desfase ausente: valor idéntico al último decimal
entre MatrikonOPC Explorer y UaExpert, `SourceTimestamp` idéntico al milisegundo,
y `ServerTimestamp` ~370 ms posterior — la separación entre hora de origen y hora
de registro, que es el criterio de "listo" de la fase.

**Por qué importa metodológicamente.** Ninguna prueba unitaria podía detectar
esto: el driver cumplía su contrato. Es un bug que solo aparece integrando contra
un sistema real, y el instrumento que lo acorraló fue el log en el borde exacto
entre los dos sistemas, no el test.