# tools/UaLoadClient/trust-setup.ps1
# Cruza la confianza entre el gateway y el UaLoadClient: copia el certificado
# publico (.der) de cada uno al almacen de confiados del otro.
# Requiere que ambos hayan arrancado al menos una vez para emitir su certificado.

$ErrorActionPreference = "Stop"

# Raiz del repo: dos niveles arriba de tools/UaLoadClient/
$repoRoot   = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$gatewayPki = Join-Path $repoRoot "pki"
$clientPki  = Join-Path $env:LOCALAPPDATA "UaLoadClient\pki"

function Copy-PublicCert {
    param([string]$FromStore, [string]$ToStore, [string]$Label)

    $srcDir = Join-Path $FromStore "own\certs"
    if (-not (Test-Path -LiteralPath $srcDir)) {
        throw "No existe $srcDir. Arranca $Label una vez para que emita su certificado."
    }

    # -Filter aparte de -LiteralPath: los nombres llevan corchetes, que como
    # patron serian comodines y no matchearian el archivo real.
    $cert = Get-ChildItem -LiteralPath $srcDir -Filter "*.der" | Select-Object -First 1
    if (-not $cert) {
        throw "No hay ningun .der en $srcDir. Arranca $Label una vez."
    }

    $dstDir = Join-Path $ToStore "trusted\certs"
    New-Item -ItemType Directory -Path $dstDir -Force | Out-Null

    $dst = Join-Path $dstDir $cert.Name
    Copy-Item -LiteralPath $cert.FullName -Destination $dst -Force
    Write-Host "OK  $Label -> $dstDir"
    Write-Host "    $($cert.Name)"
}

Write-Host "Gateway PKI: $gatewayPki"
Write-Host "Cliente PKI: $clientPki"
Write-Host ""

Copy-PublicCert -FromStore $clientPki  -ToStore $gatewayPki -Label "UaLoadClient"
Copy-PublicCert -FromStore $gatewayPki -ToStore $clientPki  -Label "Gateway"

Write-Host ""
Write-Host "Confianza cruzada lista."