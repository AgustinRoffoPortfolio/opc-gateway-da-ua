# Operación

Cómo se levanta el gateway, qué mirar cuando algo falla, y las anomalías
observadas contra sistemas reales.

> Documento en construcción. El procedimiento de arranque y el rollback se
> completan en la Fase 7. Por ahora acumula hallazgos de campo.

## Anomalías observadas

### `SourceTimestamp` atrasado ~7 minutos (Fase 2)

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

**El patrón, sin causa confirmada.** Las dos veces el desfase apareció con el
simulador sin clientes DA activos fuera del gateway, y las dos veces desapareció
al agregar un item en MatrikonOPC Explorer. En la segunda ocurrencia Explorer
mostraba la hora correcta mientras el gateway recibía la atrasada. La hipótesis
de trabajo es que el simulador no refresca los timestamps de sus items sin
lectura activa de por medio, y que el gateway solo no alcanza para mantenerlo
despierto. No se confirmó, y no se persiguió más: el driver ya estaba descartado
por medición y el objetivo de la fase no era diagnosticar el simulador.

**Estado verificado.** Con el desfase ausente: valor idéntico al último decimal
entre MatrikonOPC Explorer y UaExpert, `SourceTimestamp` idéntico al milisegundo,
y `ServerTimestamp` ~370 ms posterior — la separación entre hora de origen y hora
de registro, que es el criterio de "listo" de la fase.

**Por qué importa metodológicamente.** Ninguna prueba unitaria podía detectar
esto: el driver cumplía su contrato. Es un bug que solo aparece integrando contra
un sistema real, y el instrumento que lo acorraló fue el log en el borde exacto
entre los dos sistemas, no el test.