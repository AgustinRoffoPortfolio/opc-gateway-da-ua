# Pendientes

Deuda conocida, con su criterio de prioridad. Un item aca es una decision
tomada de no hacerlo todavia, no un olvido. Si algo se cierra, se borra de
esta lista y se documenta donde corresponda.

Estado al 24/08/2026, despues de la Fase 7.

## Correctitud

### `publish/appsettings.json` desincronizado

Hay dos `appsettings.json`: `src/Gateway.Host/` (el bueno) y `publish/`
(una foto vieja). El de `publish/` tiene `AutoAcceptUntrustedCertificates:
true` — que contradice lo que cerro la Fase 7 —, no tiene
`EnableUnsecureEndpoint`, y su `EndpointUrl` usa `localhost` en vez de
`127.0.0.1`.

Hoy no participa porque el gateway se levanta con `dotnet run`. Molesta
cuando se genere un `.exe` o un release en GitHub, que es cuando hay que
atenderlo.

Sin averiguar: si `publish/` esta versionado (`git ls-files publish/`).
Un directorio de salida commiteado es una segunda fuente de verdad.

### El aviso de dominio del certificado

Ver `operacion.md`, seccion "El log dice domain not listed en cada
conexion". Causa no confirmada, no bloquea, y probarlo exige tocar el bind
que la Fase 7 cerro a proposito.

### El generador miente cuando falla

`tools/Generate-LoadTestTags.ps1` imprime `Tags generados : 500` aunque no
haya escrito un solo byte: arma todo en memoria y el resumen final no
depende de que la escritura haya salido bien. Se vio al arreglar las rutas
—fallaron las tres escrituras y el resumen decia que todo bien—. Falta un
chequeo de que los archivos existan antes de declarar exito.

### El CSV del gateway y el del cliente coinciden por casualidad

`config/tags.example.csv` tiene los mismos 500 nombres que genera
`Generate-LoadTestTags.ps1 -TagCount 500`, pero nada garantiza que sigan
coincidiendo. Una corrida con N distinto de 500 falla de forma confusa
—items que el cliente no encuentra, uno por uno— si no se apunta tambien
el gateway al CSV generado. Documentado en
`pruebas-carga-como-correr.md`; sin resolver en el codigo.

## Higiene

### Certificados huerfanos en las PKI

Ninguno molesta funcionalmente: la validacion se hace contra el que
presenta el cliente. Es higiene — un certificado inservible en el almacen
de confiados es lo que uno no quiere encontrar al auditar.

- `pki/trusted/certs/`: el `.der` del UaLoadClient de 1024 bits
  (`51703DEF...`), inservible desde que se subio a 2048.
- `pki/rejected/certs/`: el mismo `51703DEF...` y uno viejo de UaExpert.
- PKI del cliente (`%LocalAppData%\UaLoadClient\pki\trusted\certs`): el
  certificado viejo del gateway (`2457AC8C...`), que quedo al regenerarlo.
  `trust-setup.ps1` copia el nuevo pero no limpia el anterior.

Borrar con `-LiteralPath`: los nombres llevan corchetes, que PowerShell
interpreta como comodines.

## Mantenimiento

### Ocho advertencias CS0618 en UaLoadClient

El stack 1.5.378.156 marco como obsoletas `ApplicationConfiguration.Validate`
(-> `ValidateAsync`), el constructor de `ApplicationInstance` (-> el que
recibe `ITelemetryContext`), `CoreClientUtils.SelectEndpoint` (->
`SelectEndpointAsync`), `Session.Create` (-> `ISessionFactory.CreateAsync`)
y `Subscription.Create`/`ApplyChanges` (-> sus variantes async).

Compila y funciona. Es deuda de mantenimiento, no un bug, en una
herramienta de tooling que no es parte del producto. **Decision a tomar,
no tarea asumida**: puede no valer la pena.

## Documentacion

### Faltan dos decisiones en `decisiones.md`

La eleccion del SDK cliente DA y la licencia MIT que arrastro al repo. Hoy
eso vive solo en el README, y son obligatorias para el reporte de cierre.

### Deuda de fases anteriores

- **Fase 2:** el video de demo esta grabado pero sin editar, y hay que
  regrabarlo. Con el bug de `FILETIME` corregido, la demo correcta muestra
  el `SourceTimestamp` pegado al reloj del DA y el `ServerTimestamp` unos
  cientos de ms despues (~370 ms medidos), no el desfase.
- **Fase 5:** falta el contador de intentos de conexion rechazados.
- **Fase 6:** falta la verificacion visual del `SourceTimestamp` en
  UaExpert. Quedaron fuera de alcance los soaks de 8 y 24 h y la
  validacion cruzada contra una referencia independiente.