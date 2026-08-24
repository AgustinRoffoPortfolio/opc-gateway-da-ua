<#
.SYNOPSIS
    Genera los dos CSV de la prueba de carga: aliases para MatrikonOPC
    y tags para el gateway. Salen consistentes por construccion.
.EXAMPLE
    .\Generate-LoadTestTags.ps1 -TagCount 500
#>
[CmdletBinding()]
param(
    [int]$TagCount = 8000,
    [int]$ScanRateMs = 1000,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\scratch')
)

# Tabla de roles: NOMBRE|TIPO|EU|MULTIPLICADOR|OFFSET
# 25 Double + 10 Boolean + 4 Int32 + 1 String = 40 tags por equipo.
$roleTable = @(
    'PRESION_ENTRADA|Double|bar|1|0'
    'PRESION_SALIDA|Double|bar|1|0'
    'TEMPERATURA_ENTRADA|Double|C|1|0'
    'TEMPERATURA_SALIDA|Double|F|1.8|32'
    'CAUDAL_INSTANTANEO|Double|m3h|1|0'
    'CAUDAL_TOTALIZADO|Double|m3|0.001|0'
    'NIVEL_TANQUE|Double|pct|0.01|0'
    'VIBRACION_X|Double|mms|0.01|0'
    'VIBRACION_Y|Double|mms|0.01|0'
    'CORRIENTE_MOTOR|Double|A|0.1|0'
    'TENSION_MOTOR|Double|V|1|0'
    'POTENCIA_ACTIVA|Double|kW|0.01|0'
    'VELOCIDAD_MOTOR|Double|rpm|1|0'
    'TORQUE|Double|Nm|0.1|0'
    'DENSIDAD|Double|kgm3|1|0'
    'VISCOSIDAD|Double|cP|0.01|0'
    'PH|Double|pH|0.001|0'
    'CONDUCTIVIDAD|Double|uScm|1|0'
    'HUMEDAD|Double|pct|0.01|0'
    'APERTURA_VALVULA|Double|pct|0.01|0'
    'SETPOINT_PRESION|Double|bar|1|0'
    'SETPOINT_TEMPERATURA|Double|C|1|0'
    'HORAS_SERVICIO|Double|h|0.1|0'
    'EFICIENCIA|Double|pct|0.01|0'
    'CONSUMO_ESPECIFICO|Double|kWhm3|0.001|0'
    'ESTADO_BOMBA|Boolean||1|0'
    'ESTADO_VALVULA|Boolean||1|0'
    'ALARMA_ALTA|Boolean||1|0'
    'ALARMA_BAJA|Boolean||1|0'
    'MODO_AUTOMATICO|Boolean||1|0'
    'PERMISO_ARRANQUE|Boolean||1|0'
    'FALLA_GENERAL|Boolean||1|0'
    'CONFIRMACION_MARCHA|Boolean||1|0'
    'SELECTOR_LOCAL_REMOTO|Boolean||1|0'
    'EN_MANTENIMIENTO|Boolean||1|0'
    'CONTADOR_ARRANQUES|Int32||1|0'
    'CONTADOR_ALARMAS|Int32||1|0'
    'CODIGO_FALLA|Int32||1|0'
    'PASO_SECUENCIA|Int32||1|0'
    'RECETA_ACTIVA|String||1|0'
)

# Los cuatro ItemID nativos de la rama Random del simulador.
$daSourceByType = @{
    'Double'  = 'Random.Real8'
    'Boolean' = 'Random.Boolean'
    'Int32'   = 'Random.Int4'
    'String'  = 'Random.String'
}

$rolesPerDevice  = $roleTable.Count
$devicesPerPlant = 20
$deviceCount     = [math]::Ceiling($TagCount / $rolesPerDevice)

$uaLines = [System.Collections.Generic.List[string]]::new()
$daLines = [System.Collections.Generic.List[string]]::new()
$stamp   = (Get-Date).ToString('yyyy-MM-dd HH:mm')

$uaLines.Add("# Generado por Generate-LoadTestTags.ps1 el $stamp - $TagCount tags")
$uaLines.Add("# Nombres inventados. Decimales con PUNTO (InvariantCulture).")
$uaLines.Add('TAG_NAME_OPC_UA;TAG_NAME_OPC_DA;DATA_TYPE;MULTIPLICADOR;OFFSET;EU;SCAN_RATE_MS;DEADBAND;ACCESS_LEVEL;DESCRIPTION;ENABLED')
$daLines.Add("# Generado por Generate-LoadTestTags.ps1 el $stamp - $TagCount aliases")

$emitted = 0
for ($device = 1; $device -le $deviceCount -and $emitted -lt $TagCount; $device++) {
    $plant     = [int][math]::Floor(($device - 1) / $devicesPerPlant) + 1
    $plantId   = 'PLANTA_{0:D2}' -f $plant
    $deviceId  = 'EQUIPO_{0:D3}' -f $device

    foreach ($role in $roleTable) {
        if ($emitted -ge $TagCount) { break }

        $name, $type, $eu, $mult, $offset = $role -split '\|'
        $daItem    = $daSourceByType[$type]
        $aliasName = "${plantId}_${deviceId}_${name}"

        # El alias vive en el grupo raiz: el ItemID resultante lleva punto inicial.
        $uaLines.Add("$plantId.$deviceId.$name;.$aliasName;$type;$mult;$offset;$eu;$ScanRateMs;0;Read;$name de $deviceId;True")
        $daLines.Add(",$aliasName,$daItem,0,0,0,0,0,,,,,,,0,Alias,0,1,,0,0")

        $emitted++
    }
}

# El directorio puede no existir en un clon recien hecho, y Resolve-Path
# falla si no esta. La API de .NET lo crea si falta, no hace nada si ya
# existe, y devuelve la ruta normalizada (sin el ".." del medio).
# Path.Combine deja pasar $OutputDir tal cual si es absoluta, y la ancla
# a la ubicacion actual si el usuario paso una relativa.
$resolvedDir = [System.IO.Directory]::CreateDirectory(
    [System.IO.Path]::Combine($PWD.ProviderPath, $OutputDir)).FullName
$uaPath = Join-Path $resolvedDir "tags-$TagCount.csv"
$daPath = Join-Path $resolvedDir "aliases-$TagCount.csv"

# ASCII a proposito: el contenido no tiene acentos y evita el BOM,
# que Matrikon podria no digerir al importar.
[System.IO.File]::WriteAllLines($uaPath, $uaLines, [System.Text.Encoding]::ASCII)
[System.IO.File]::WriteAllLines($daPath, $daLines, [System.Text.Encoding]::ASCII)

Write-Host "Tags generados : $emitted"
Write-Host "Equipos        : $($device - 1) de $deviceCount previstos"
Write-Host "Plantas        : $plantId (ultima)"
Write-Host "CSV tags UA    : $uaPath"
Write-Host "CSV aliases DA : $daPath"