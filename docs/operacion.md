# Operación

Cómo se levanta el gateway, qué mirar cuando algo falla, y las anomalías
observadas contra sistemas reales.

> Documento en construcción. El procedimiento de arranque y el rollback se
> completan en la Fase 7. Por ahora acumula hallazgos de campo.

## Anomalías observadas

### `SourceTimestamp` atrasado — RESUELTO en Fase 6

Durante las fases 2 a 5, el `SourceTimestamp` llegaba intermitentemente ~7 minutos
atrasado. **La causa es un bug de conversión de `FILETIME` en el SDK cliente DA,
no del simulador.** El gateway lo corrige desde la Fase 6 en
`Gateway.Da.SdkTimestamp.Correct()`.

La historia completa, con la aritmética, la evidencia medida y el error de método
que mantuvo viva la hipótesis equivocada durante tres fases, está en
**[bug-filetime-sdk.md](bug-filetime-sdk.md)**.

**Qué se descartó correctamente en su momento** (sigue siendo válido): drift de
reloj, cambios de hora del sistema, suspensión, reinicio del simulador, y
configuración de aliases. También se instrumentó el borde del driver y se
comprobó que transmitía fielmente lo que recibía — correcto, pero incompleto: el
SDK quedaba una capa por debajo del punto donde se estaba midiendo.

**Qué hacer si reaparece.** Buscar timestamps terminados en `.5032704`. Esa firma
es exclusiva de este bug. Si aparece, la corrección no se está aplicando en algún
camino de lectura nuevo: todos tienen que pasar por `SdkTimestamp.Correct()`, y
exactamente una vez (no es idempotente).

**Cómo grabar la demo.** Con el bug corregido, el `SourceTimestamp` sigue al reloj
y el `ServerTimestamp` queda unos cientos de ms después. Esa separación chica es
la que hay que mostrar: son dos relojes distintos, y el de origen no se pisa. Un
atraso de 7 minutos en pantalla ya no es una curiosidad del simulador, es el bug
sin corregir.

**Consecuencia para la Fase 4, sin cambios.** La degradación por antigüedad se
sigue midiendo con `LastUpdateUtc` (reloj del gateway) y no con `SourceTimestamp`.
El motivo original ya no aplica, pero el criterio es correcto igual: el
`SourceTimestamp` lo produce un sistema ajeno y no hay garantía de que su reloj
esté sincronizado con el nuestro.

**Por qué importa metodológicamente.** Ninguna prueba unitaria del gateway podía
detectarlo: el driver cumplía su contrato. Lo que lo acorraló fue medir el
desfase y reconocer el número — 429,4967296 s es 2³² ticks, la firma de un error
de signo. La instrumentación en el borde entre los dos sistemas fue el
instrumento correcto; lo que faltó fue mirar una capa más abajo.

## Operar el simulador MatrikonOPC durante las pruebas

Tres comportamientos que cuestan una sesión entera si se descubren en el momento.

### El servidor DA no es la ventana que se ve en el escritorio

Es un componente COM **out-of-process**: Windows lo lanza a demanda cuando un
cliente pide el ProgID. La ventana que se abre desde el menú de inicio es el
*configurador* (`PSTCFG.exe`), no el servidor (`OPCSim.exe`). Cerrarla no deja al
servidor indisponible.

- Para simular **DA caído**: `Stop-Process -Name OPCSim -Force`. COM lo relanza
  solo en el siguiente intento de conexión, lo cual es bueno: la recuperación no
  depende de que nadie levante nada a mano.
- Para simular **DA ausente de verdad**: apuntar el gateway a un ProgID
  inexistente en `appsettings.json` (se usó `Matrikon.OPC.NoExiste.1`). Da
  `0x80040154 CoCreateInstanceEx: Clase no registrada`.

Cuando el servidor muere, `Read` tira `0x800706BA` (RPC server unavailable) y el
objeto COM queda inservible: no es un error transitorio. Por eso el gateway
recrea el driver entero en vez de reintentar la lectura.

### El servidor relanzado vuelve vacío

Los aliases importados viven en el archivo de configuración, no en el proceso. Al
matar `OPCSim`, COM lo relanza con la configuración por defecto y los ItemIDs
importados dejan de existir: el gateway pide items que del otro lado ya no están y
el alta se rechaza, correctamente.

Para volver a poblarlo hay que cargar el escenario de nuevo en el configurador
(File → Open sobre el `.opcsim.xml`). El gateway se reengancha solo en el
siguiente reintento, sin reiniciarlo.

Esto no se nota con items nativos del simulador (`Random.Real8` y similares), que
existen siempre. Aparece con escenarios de carga importados.

### El configurador crashea si se lo usa con el servidor muerto

Si se intenta cargar un `.opcsim.xml` con `OPCSim` matado, el configurador da
`Error 0x800706BA occurred loading configuration from file` y a continuación un
access violation en `PSTCFG.exe`, quedando inconsistente (`Clients: -1`,
`Server Time: n/a`).

Salida: cerrar el configurador —forzándolo con `Stop-Process -Name PSTCFG -Force`
si no responde— y abrirlo de nuevo. Al arrancar limpio relanza el servidor por COM
y la carga funciona. Es un bug del configurador, no del gateway.

Al cargar una configuración con el gateway conectado, Matrikon avisa que los
items OPC se invalidan. Es el mismo escenario que en planta produce una recarga de
configuración del servidor DA, y el gateway lo resuelve con el reintento de altas.

## Empaquetar el gateway para distribuir

El paquete permite correr el gateway en una máquina sin Visual Studio, sin el
repo y sin .NET instalado. Es una carpeta comprimida, no un instalador: se
descomprime y se ejecuta `Gateway.Host.exe`.

### Publicar

```powershell
dotnet publish .\src\Gateway.Host -c Release -r win-x86 --self-contained true -o .\publish
```

`win-x86` no es opcional: el driver DA es COM de 32 bits y el ejecutable manda
sobre todas las bibliotecas que carga.

**Self-contained** porque el runtime de .NET viaja adentro. La alternativa exige
que la máquina destino tenga instalado el runtime **x86** específicamente —el x64
no sirve—, que es el tipo de requisito donde alguien que quería probar el gateway
diez minutos abandona. Precio: ~110 MB de carpeta.

**Sin `PublishTrimmed`.** El stack de la OPC Foundation y el interop COM resuelven
tipos por reflection; el trimmer los borra sin avisar y la falla aparece en
runtime, no al compilar.

`dotnet publish -o` no limpia el destino: sobreescribe lo que coincide y deja lo
demás. Un publish anterior con otra configuración sobrevive mezclado, y peor,
puede hacer que el paquete arranque en la máquina de desarrollo por un archivo
que en la máquina destino no va a estar. Borrar `publish/` antes de cada corrida.

### Armar el paquete

Sobre la carpeta publicada:

- **Borrar `pki/`.** Los certificados se generan solos en el primer arranque.
  Mandar los propios arrastra el nombre de host de la máquina de desarrollo.
- **Borrar los `.pdb`.** Además de ser ruido, sus stack traces exponen las rutas
  absolutas del disco de desarrollo.
- **Agregar** el `.opcsim.xml` del escenario de demo, el CSV de aliases y un
  `LEEME.txt` con requisitos, puesta en marcha y qué se debería ver.

`config/demo-10.opcsim.xml` está versionado aunque sea un archivo generado por
el configurador de Matrikon: son 2 KB y evita tener que abrirlo cada vez que se
arma un paquete. Se regenera importando `config/aliases.example.csv` con
`File → Import Aliases` y guardando con `File → Save As...`. Al importar sobre
una configuración ya abierta los aliases se suman a los que había, así que
conviene partir de una configuración vacía —si no, el escenario de demo termina
guardado junto con los 8.000 de la prueba de carga.

### Qué se verificó y qué no

La prueba no es que el publish termine sin error: es **descomprimir el paquete en
una carpeta cualquiera fuera del repo y que arranque**. Ese recorrido está
verificado, y fue el que expuso el problema de rutas de la decisión 21 — el
gateway resolvía la PKI subiendo hasta el archivo de solución, que en una máquina
sin el repo no existe.

Lo que **no** está verificado en limpio son los dos primeros pasos del LEEME:
instalar el simulador y cargar el escenario en un Windows donde Matrikon nunca
estuvo. La máquina de desarrollo ya lo tenía instalado y con los aliases
importados, así que esa parte del procedimiento está escrita pero no probada.

### Firewall

Al primer arranque Windows pide permiso de red. **Se puede cancelar**: el
servidor UA y la página de diagnóstico escuchan en `localhost`, y ese tráfico no
lo filtra el firewall. Conviene decirlo en el LEEME para que no se lea como que
algo falló.

Cancelar no es neutro: Windows crea una regla de bloqueo explícita, igual que
aceptar crea una de permiso. Se listan y se borran así (el borrado necesita
PowerShell como administrador):

```powershell
Get-NetFirewallApplicationFilter |
  Where-Object { $_.Program -like "*Gateway.Host.exe*" } |
  Get-NetFirewallRule |
  Select-Object DisplayName, Direction, Action, Profile
```


## Anomalía conocida: errores de certificado repetidos en consola

**Síntoma.** La consola del gateway muestra, cada ~5 segundos y de forma
indefinida, un bloque de error como este:

[ERR] Could not verify security on OpenSecureChannel request.
The receiver's certificate thumbprint is not valid.
[80120000] (BadCertificateInvalid) 'The receiver's certificate thumbprint is not valid.'
[ERR] ChannelId N: ForceChannelFault due to Could not verify security on OpenSecureChannel request..


El `ChannelId` se incrementa en cada repetición. Suele venir acompañado de
`SERVER - Service Fault Occurred. Reason=BadMessageNotAvailable`.

**Qué NO significa.** No indica un problema con el certificado propio del
gateway, ni con la PKI, ni con la configuración de seguridad. Tampoco lo
resuelve tener `auto-aceptar` habilitado: esa opción controla si el gateway
confía en el certificado *del cliente*, y acá el rechazo va por otro lado.

**Causa.** En el mensaje `OpenSecureChannel`, el cliente incluye el thumbprint
del certificado *del servidor* que tiene guardado de una sesión anterior —es su
forma de decir "el receptor de este mensaje debería ser este de acá". El gateway
compara ese thumbprint contra el certificado que está usando y, si no coincide,
corta el canal. Es decir: **hay un cliente UA con una identidad vieja del
servidor cacheada**, típicamente de una instalación previa o de otro servidor que
alguna vez ocupó el mismo endpoint. El cliente reintenta solo, en loop, y el
gateway lo rechaza correctamente cada vez.

**Cómo verificarlo.** Con el gateway levantado, ver qué procesos y qué máquinas
tienen conexiones al puerto UA:

```powershell
Get-NetTCPConnection -LocalPort 4840 -ErrorAction SilentlyContinue |
  Select-Object State, LocalAddress, RemoteAddress, OwningProcess
```

Para ver también las conexiones que ya cerraron (estados `Time Wait`), que es
donde suele quedar el rastro del cliente que reintenta, conviene
[TCPView](https://learn.microsoft.com/sysinternals/downloads/tcpview) filtrando
por `4840`: muestra proceso, dirección remota y timestamps en una sola vista.

Dos cosas a mirar en esa salida:

- **Si la dirección remota no es `127.0.0.1`**, el cliente está en otra máquina de
  la red. El gateway escucha hoy en `0.0.0.0`, o sea en todas las interfaces
  (ver Fase 7: el bind debe pasar a ser configurable).
- **Cuántos certificados propios tiene el gateway.** Debe haber exactamente uno:

```powershell
  Get-ChildItem -Recurse .\pki\own\certs | Select-Object Name, LastWriteTime
```

  Si hay dos o más, el problema es otro: el gateway regeneró su certificado —por
  ejemplo al correrse desde una ruta distinta, ya que la PKI es relativa al
  directorio de trabajo— y entonces el thumbprint desactualizado lo tienen
  todos los clientes legítimos.

**Resolución.** Con un solo certificado propio, no hay nada que corregir del lado
del gateway: está rechazando a un cliente que se identifica mal, que es el
comportamiento esperado. Las salidas son borrar el certificado cacheado del store
de confianza de ese cliente, o apagarlo si nadie lo usa. Las conexiones huérfanas
se limpian solas por timeout y los errores cesan.

**Pendiente.** Este ruido no debería vivir en la consola. En la Fase 5, los
intentos de conexión rechazados pasan a ser un contador en la página de
diagnóstico, agrupado por motivo. Un rechazo correcto es un evento contable, no
un error.