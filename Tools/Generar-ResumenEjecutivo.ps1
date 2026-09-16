<#
.SYNOPSIS
    Ejecuta la suite de verificación y genera un resumen ejecutivo en Markdown.

.DESCRIPTION
    Corre las pruebas del proyecto y traduce el archivo TRX a un resumen legible en
    docs/VERIFICACION-RESUMEN.md: versión del plugin, versión de Jellyfin validada, entorno detectado y el
    resultado de cada verificación.

    Cuando no se indica una cadena de conexión, las pruebas que necesitan PostgreSQL se marcan como
    omitidas y el resumen lo refleja, de modo que el documento nunca exagera lo que se comprobó.

.PARAMETER ConnectionString
    Cadena de conexión de un usuario administrador de PostgreSQL, usada únicamente para crear y eliminar
    bases de datos desechables. Por defecto toma la variable de entorno POSTGRES_TEST_CONNECTION.

.PARAMETER OutputPath
    Ruta del resumen a generar. Por defecto docs/VERIFICACION-RESUMEN.md.

.EXAMPLE
    $env:POSTGRES_TEST_CONNECTION = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=...'
    ./Tools/Generar-ResumenEjecutivo.ps1
#>
[CmdletBinding()]
param(
    [string]$ConnectionString = $env:POSTGRES_TEST_CONNECTION,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $repoRoot 'Tests/Jellyfin.Database.Providers.Postgres.Tests/Jellyfin.Database.Providers.Postgres.Tests.csproj'
$pluginProject = Join-Path $repoRoot 'Jellyfin.Database.Providers.Postgres/Jellyfin.Database.Providers.Postgres.csproj'
$resultsDir = Join-Path $repoRoot 'Tests/TestResults'
$trxName = 'verificacion.trx'
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'docs/VERIFICACION-RESUMEN.md' }

if ($ConnectionString) {
    $env:POSTGRES_TEST_CONNECTION = $ConnectionString
    Write-Host 'PostgreSQL configurado: se ejecutarán también las pruebas de integración.' -ForegroundColor Cyan
}
else {
    Write-Host 'Sin POSTGRES_TEST_CONNECTION: las pruebas de integración se omitirán.' -ForegroundColor Yellow
}

Write-Host 'Ejecutando la suite de verificación...' -ForegroundColor Cyan
dotnet test $testProject --configuration Release --logger "trx;LogFileName=$trxName" --results-directory $resultsDir | Out-Host
$testExitCode = $LASTEXITCODE

$trxPath = Join-Path $resultsDir $trxName
if (-not (Test-Path -LiteralPath $trxPath)) {
    throw "No se generó el archivo de resultados: $trxPath"
}

[xml]$trx = Get-Content -LiteralPath $trxPath
$ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
$ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

$results = @(
    $trx.SelectNodes('//t:UnitTestResult', $ns) | ForEach-Object {
        # className agrupa las verificaciones por área; algunos runners no lo rellenan, así que se deduce
        # del nombre completo del test quitando el parámetro de las teorías.
        $className = $_.className
        if (-not $className) { $className = ($_.testName -split '\(')[0] -split '\.' | Select-Object -Index 5 }
        else { $className = ($className -split '\.')[-1] }

        [pscustomobject]@{
            Name    = $_.testName
            Class   = $className
            Outcome = $_.outcome
        }
    }
)

$total = $results.Count
$failed = @($results | Where-Object { $_.Outcome -eq 'Failed' })
$skipped = @($results | Where-Object { $_.Outcome -eq 'NotExecuted' })
$passed = $total - $failed.Count - $skipped.Count

# El nombre de la clase agrupa las verificaciones por área funcional.
$areas = $results | Group-Object Class | Sort-Object Name | ForEach-Object {
    $bad = @($_.Group | Where-Object { $_.Outcome -eq 'Failed' }).Count
    $skippedInArea = @($_.Group | Where-Object { $_.Outcome -eq 'NotExecuted' }).Count
    $state = if ($bad -gt 0) { "❌ $bad con error" }
        elseif ($skippedInArea -eq $_.Count) { '⚠️ omitida' }
        elseif ($skippedInArea -gt 0) { "✅ $($_.Count - $skippedInArea)/$($_.Count) (resto omitidas)" }
        else { "✅ $($_.Count)/$($_.Count)" }

    [pscustomobject]@{ Area = $_.Name; Verificaciones = $_.Count; Resultado = $state }
}

$pluginVersion = (Select-String -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value
$jellyfinVersion = (Select-String -LiteralPath $pluginProject -Pattern '<JellyfinVersion>([^<]+)</JellyfinVersion>').Matches.Groups[1].Value
$databaseName = if ($ConnectionString) {
    (($ConnectionString -split ';' | Where-Object { $_ -match '^\s*Database\s*=' }) -replace '^\s*Database\s*=\s*', '').Trim()
}
else {
    'no configurada'
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Resumen ejecutivo de verificación')
$lines.Add('')
$lines.Add('> Documento generado automáticamente por `Tools/Generar-ResumenEjecutivo.ps1` a partir del resultado')
$lines.Add('> real de la suite de pruebas. No se edita a mano.')
$lines.Add('')
$lines.Add('| Dato | Valor |')
$lines.Add('| --- | --- |')
$lines.Add("| Fecha de ejecución | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') |")
$lines.Add("| Versión del plugin | $pluginVersion |")
$lines.Add("| Jellyfin validado | $jellyfinVersion |")
$lines.Add("| Base de datos de pruebas | $databaseName |")
$lines.Add("| Entorno | $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription) · SDK $((dotnet --version).Trim()) |")
$lines.Add('')
$lines.Add('## Resultado global')
$lines.Add('')
$lines.Add('| Verificaciones | Correctas | Con error | Omitidas |')
$lines.Add('| --- | --- | --- | --- |')
$lines.Add("| $total | $passed | $($failed.Count) | $($skipped.Count) |")
$lines.Add('')
$lines.Add('## Resultado por área')
$lines.Add('')
$lines.Add('| Área | Verificaciones | Resultado |')
$lines.Add('| --- | --- | --- |')
foreach ($area in $areas) {
    $lines.Add("| $($area.Area) | $($area.Verificaciones) | $($area.Resultado) |")
}

$lines.Add('')
$lines.Add('## Detalle')
$lines.Add('')
$lines.Add('| Verificación | Resultado |')
$lines.Add('| --- | --- |')
foreach ($result in ($results | Sort-Object Name)) {
    $icon = switch ($result.Outcome) {
        'Passed' { '✅' }
        'NotExecuted' { '⚠️ omitida' }
        default { '❌ con error' }
    }
    $lines.Add("| $($result.Name -replace '^Jellyfin\.Database\.Providers\.Postgres\.Tests\.', '') | $icon |")
}

if ($skipped.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Verificaciones omitidas')
    $lines.Add('')
    $lines.Add('Estas pruebas necesitan un PostgreSQL accesible y se omitieron porque la ejecución no recibió')
    $lines.Add('`POSTGRES_TEST_CONNECTION`. El resultado global **no** las cuenta como correctas.')
    $lines.Add('')
    foreach ($result in $skipped) {
        $lines.Add("- $($result.Name -replace '^Jellyfin\.Database\.Providers\.Postgres\.Tests\.', '')")
    }
}

$lines.Add('')
$lines.Add('## Cómo reproducirlo')
$lines.Add('')
$lines.Add('```bash')
$lines.Add('# Sólo verificaciones sin base de datos')
$lines.Add('dotnet test Tests/Jellyfin.Database.Providers.Postgres.Tests')
$lines.Add('')
$lines.Add('# Verificaciones completas, incluida la integración con PostgreSQL')
$lines.Add('$env:POSTGRES_TEST_CONNECTION = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=***"')
$lines.Add('./Tools/Generar-ResumenEjecutivo.ps1')
$lines.Add('```')
$lines.Add('')
$lines.Add('Las pruebas de integración crean su propia base de datos desechable y la eliminan al terminar:')
$lines.Add('la cadena de conexión indicada se usa únicamente para `CREATE DATABASE` / `DROP DATABASE`.')
$lines.Add('')

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}
$lines -join [Environment]::NewLine | Set-Content -LiteralPath $OutputPath -Encoding UTF8

Write-Host "Resumen generado: $OutputPath" -ForegroundColor Green
Write-Host "Resultado: $passed correctas, $($failed.Count) con error, $($skipped.Count) omitidas de $total." -ForegroundColor Cyan

exit $testExitCode
