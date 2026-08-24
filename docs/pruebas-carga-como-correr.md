# Escenarios de prueba de carga

Como preparar y correr un escenario de N tags. Los resultados de las
corridas —escalones, latencias, soak— estan en
[`pruebas-carga.md`](pruebas-carga.md); este documento es el procedimiento
que los genera.

## La variable de entorno `Ua__TagsCsvPath`

El gateway lee la ruta del CSV de tags de `Ua:TagsCsvPath`, que en
`src/Gateway.Host/appsettings.json` apunta a `config/tags.example.csv`.
Para las pruebas de carga esa ruta se pisa con la variable de entorno
**`Ua__TagsCsvPath`** (doble guion bajo = anidamiento de seccion en la
configuracion de .NET). La variable gana sobre el JSON, asi que permite
cambiar de escenario sin editar ningun archivo versionado.

Tres advertencias, las tres ya causaron confusion:

- **Se lee una sola vez, al arrancar.** Hay que setearla *antes* de
  levantar el gateway; cambiarla con el proceso corriendo no hace nada.
- **No se hereda entre terminales.** Si el gateway se levanta en una
  terminal y el cliente de carga en otra, la variable tiene que estar
  seteada en la terminal donde corre el gateway.
- **Con `$env:` vive solo en esa sesion** y se pierde al cerrarla. Eso es
  deliberado: es un interruptor de escenario, no configuracion permanente.
  El precio es que no queda rastro de ella en ningun lado, y por eso esta
  documentada aca.

## Cambiar de escenario son tres acciones coordinadas

Generar los CSV, importar los aliases en el simulador y apuntar la
variable. Si una de las tres queda desfasada **el gateway arranca igual**
y el sintoma aparece despues, en forma de items que el cliente no
encuentra o de tags en calidad mala. No hay un error claro al inicio.

Para un escenario de 4000 tags:

```powershell
# 1. Generar los dos CSV (salen consistentes por construccion).
.\tools\Generate-LoadTestTags.ps1 -TagCount 4000

# 2. Importar scratch\aliases-4000.csv en MatrikonOPC:
#    File -> Import Aliases. Sin esto los ItemIDs no existen.

# 3. Apuntar el gateway al CSV de tags, antes de arrancarlo.
$env:Ua__TagsCsvPath = "$PWD\scratch\tags-4000.csv"
dotnet run --project .\src\Gateway.Host
```

El log de arranque tiene que decir `Tags cargados: 4000 validos, 0 con
error`. Si dice 500, la variable no llego al proceso y esta leyendo el
`config/tags.example.csv` del JSON.

## Correr el cliente de carga

Con el gateway ya levantado, desde otra terminal:

```powershell
Push-Location .\tools\UaLoadClient\bin\Debug\net10.0
.\UaLoadClient.exe "opc.tcp://127.0.0.1:4840/GatewayDaUa" 1 "..\..\..\..\..\scratch\tags-4000.csv" 5
Pop-Location
```

Los argumentos son posicionales: endpoint, cantidad de clientes, ruta al
CSV, minutos. Si se omite el CSV, el cliente usa `scratch/tags-500.csv`
derivandolo de la raiz del repo. Omitir un argumento obliga a omitir los
siguientes, asi que para cambiar los minutos hay que pasar el CSV igual.

Tiene que imprimir `Endpoint elegido: SignAndEncrypt / ...`, los items
suscriptos, y al cerrar un conteo de notificaciones y muestras de
latencia. Si la latencia sale "Sin muestras", el nodo
`Gateway.Performance.CacheStampUtc` no esta llegando: la herramienta
conecta pero no mide.

## Los CSV generados no se versionan

`scratch/` esta en `.gitignore`. Los CSV son salida regenerable con un
comando, y versionar salida generada crea una segunda fuente de verdad
que envejece. Tanto el generador como `tools/UaLoadClient` derivan la
ruta de la raiz del repo, de modo que un clon recien hecho funciona sin
editar rutas: el generador la arma desde `$PSScriptRoot`, y el cliente
sube desde el ejecutable hasta encontrar la carpeta `.git`.