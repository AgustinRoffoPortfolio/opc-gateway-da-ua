param(
    [int]$DurationMinutes = 10,
    [int]$IntervalSeconds = 30,
    [string]$Label = "corridaA",
    [string]$OutDir = "C:\Users\agust\Portfolio\scratch"
)

# Cultura invariante: en es-AR los decimales salen con coma y rompen el CSV.
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$outFile = Join-Path $OutDir "memoria-$Label.csv"
$samples = @()

Write-Host "Esperando el proceso Gateway.Host..."
while (-not (Get-Process -Name Gateway.Host -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 2
}
$p = Get-Process -Name Gateway.Host
$deadline = (Get-Date).AddMinutes($DurationMinutes)
Write-Host "PID $($p.Id). Muestreando cada $IntervalSeconds s durante $DurationMinutes min."

while ((Get-Date) -lt $deadline) {
    $p.Refresh()
    if ($p.HasExited) { Write-Warning "El proceso termino antes de tiempo."; break }
    $s = [pscustomobject]@{
        TimestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1).ToString($inv)
        PrivateMB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 1).ToString($inv)
        Handles      = $p.HandleCount
        Threads      = $p.Threads.Count
    }
        $samples += $s
    $s | Export-Csv -Path $outFile -NoTypeInformation -Encoding UTF8 -Append
    Write-Host "$($s.TimestampUtc) | priv $($s.PrivateMB) MB | WS $($s.WorkingSetMB) MB | handles $($s.Handles) | threads $($s.Threads)"
    Start-Sleep -Seconds $IntervalSeconds
}

$samples | Export-Csv -Path $outFile -NoTypeInformation -Encoding UTF8
Write-Host "Guardado en $outFile"