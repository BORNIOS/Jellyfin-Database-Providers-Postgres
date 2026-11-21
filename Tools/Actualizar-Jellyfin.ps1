<#
.SYNOPSIS
    Adapta el proveedor a una versión nueva de Jellyfin: paquetes, ABI, versión, migración EF y pruebas.

.DESCRIPTION
    Automatiza lo que hasta ahora se hacía a mano en cada versión de Jellyfin (12.1, 12.4):

      1. Averigua la versión objetivo (la última estable de Jellyfin.Controller en NuGet, o -JellyfinVersion).
      2. Lee del nuspec de Jellyfin.Database.Implementations la versión de EF Core que trae el servidor y elige el
         Npgsql.EntityFrameworkCore.PostgreSQL estable más reciente de esa misma serie.
      3. Actualiza los .csproj, build.yaml (targetAbi y version) y Directory.Build.props.
      4. Compila con IncludeJellyfinRuntime=true y, si el modelo de Jellyfin cambió, genera la migración
         Jellyfin<versión> con dotnet-ef y regenera Resources/schema.sql.
      5. Compila en Release y ejecuta la suite (con PostgreSQL si hay cadena de conexión).
      6. Lista los cambios del código de Jellyfin que afectan a un proveedor (interfaz del proveedor, modelo,
         migraciones de código) usando la API de comparación de GitHub.

    Escribe un resumen en Markdown (cuerpo del PR) y, en GitHub Actions, las salidas del paso. El script nunca
    hace commit ni push: eso lo decide quien lo ejecuta o el workflow.

    Versión del plugin: cambio de versión mayor de Jellyfin -> mayor+1; cambio menor -> menor+1; parche -> parche+1.

.PARAMETER JellyfinVersion
    Versión de los paquetes Jellyfin.* (por ejemplo 12.5.0). Por defecto, la última estable publicada en NuGet.

.PARAMETER ConnectionString
    Cadena de conexión de administrador de PostgreSQL para las pruebas de integración. Por defecto
    POSTGRES_TEST_CONNECTION. Sin ella las pruebas de integración se omiten y el resumen lo indica.

.PARAMETER SummaryPath
    Dónde escribir el resumen. Por defecto artifacts/actualizacion-jellyfin.md.

.PARAMETER SkipTests
    No ejecuta la suite (solo para pruebas del propio script).

.EXAMPLE
    ./Tools/Actualizar-Jellyfin.ps1
    ./Tools/Actualizar-Jellyfin.ps1 -JellyfinVersion 12.5.0
#>
[CmdletBinding()]
param(
    [string]$JellyfinVersion,
    [string]$ConnectionString = $env:POSTGRES_TEST_CONNECTION,
    [string]$SummaryPath,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pluginDir = Join-Path $repoRoot 'Jellyfin.Database.Providers.Postgres'
$pluginProject = Join-Path $pluginDir 'Jellyfin.Database.Providers.Postgres.csproj'
$testProject = Join-Path $repoRoot 'Tests/Jellyfin.Database.Providers.Postgres.Tests/Jellyfin.Database.Providers.Postgres.Tests.csproj'
$buildYaml = Join-Path $repoRoot 'build.yaml'
$buildProps = Join-Path $repoRoot 'Directory.Build.props'
$migrationsDir = Join-Path $pluginDir 'Data/Migrations'
$schemaSql = Join-Path $pluginDir 'Resources/schema.sql'
if (-not $SummaryPath) { $SummaryPath = Join-Path $repoRoot 'artifacts/actualizacion-jellyfin.md' }

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$nuget = 'https://api.nuget.org/v3-flatcontainer'

function Write-Output-Var([string]$Name, [string]$Value) {
    if ($env:GITHUB_OUTPUT) {
        if ($Value -match "`n") {
            $delimiter = "EOF_$([guid]::NewGuid().ToString('N'))"
            Add-Content -Path $env:GITHUB_OUTPUT -Value "$Name<<$delimiter`n$Value`n$delimiter" -Encoding utf8
        } else {
            Add-Content -Path $env:GITHUB_OUTPUT -Value "$Name=$Value" -Encoding utf8
        }
    }
}

function Get-StableVersions([string]$PackageId) {
    $index = Invoke-RestMethod -Uri "$nuget/$($PackageId.ToLowerInvariant())/index.json"
    $index.versions |
        Where-Object { $_ -notmatch '-' } |
        ForEach-Object { [version]$_ } |
        Sort-Object
}

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText($Path) }

# Keeps the BOM state of the file: the repository has both kinds and the diff should only show the real change.
function Write-Text([string]$Path, [string]$Content) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($hasBom))
}

function Remove-Bom([string]$Path) {
    [System.IO.File]::WriteAllText($Path, [System.IO.File]::ReadAllText($Path), $utf8NoBom)
}

function Set-PackageVersion([string]$Path, [string]$PackageId, [string]$Version) {
    $content = Read-Text $Path
    $pattern = '(<PackageReference\s+Include="' + [regex]::Escape($PackageId) + '"\s+Version=")[^"$]+(")'
    $updated = [regex]::Replace($content, $pattern, "`${1}$Version`${2}")
    if ($updated -ne $content) { Write-Text $Path $updated }
}

function Invoke-Dotnet([string[]]$Arguments, [switch]$AllowFailure) {
    Write-Host "> dotnet $($Arguments -join ' ')"
    $output = & dotnet @Arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $output | Write-Host
    if ($code -ne 0 -and -not $AllowFailure) { throw "dotnet $($Arguments[0]) falló con código $code" }
    [pscustomobject]@{ ExitCode = $code; Output = $output }
}

# ── 1. Versions ───────────────────────────────────────────────────────────────────────────────────
$pluginXml = Read-Text $pluginProject
$current = [version]([regex]::Match($pluginXml, '<JellyfinVersion>([^<]+)</JellyfinVersion>').Groups[1].Value)
$target = if ($JellyfinVersion) { [version]$JellyfinVersion } else { Get-StableVersions 'Jellyfin.Controller' | Select-Object -Last 1 }
Write-Host "Jellyfin actual: $current  objetivo: $target"

if ($target -le $current) {
    Write-Host 'El proveedor ya compila contra esa versión o una posterior; nada que hacer.'
    Write-Output-Var 'changed' 'false'
    Write-Output-Var 'jellyfin_version' "$target"
    return
}

$nuspec = [xml](Invoke-WebRequest -Uri "$nuget/jellyfin.database.implementations/$target/jellyfin.database.implementations.nuspec").Content
$efDependency = $nuspec.package.metadata.dependencies.SelectNodes('//*[local-name()="dependency"]') |
    Where-Object { $_.id -eq 'Microsoft.EntityFrameworkCore.Relational' } |
    Select-Object -First 1
if (-not $efDependency) { throw "El nuspec de Jellyfin.Database.Implementations $target no declara EF Core." }
$efVersion = [version](($efDependency.version -replace '[\[\]\(\)]', '').Split(',')[0])

$currentNpgsql = [version]([regex]::Match($pluginXml, 'Include="Npgsql\.EntityFrameworkCore\.PostgreSQL"\s+Version="([^"]+)"').Groups[1].Value)
# The plugin ships Npgsql's EF provider but runs on the server's EF Core: a provider that needs a newer EF Core than
# the server has fails at runtime (Npgsql EF 10.0.4 needs EF 10.0.15, Jellyfin 12.4 ships 10.0.14).
function Get-EfLowerBound([version]$NpgsqlVersion) {
    $spec = [xml](Invoke-WebRequest -Uri "$nuget/npgsql.entityframeworkcore.postgresql/$NpgsqlVersion/npgsql.entityframeworkcore.postgresql.nuspec").Content
    $dependency = $spec.SelectNodes('//*[local-name()="dependency"]') |
        Where-Object { $_.id -eq 'Microsoft.EntityFrameworkCore.Relational' } |
        Select-Object -First 1
    [version](($dependency.version -replace '[\[\]\(\)]', '').Split(',')[0])
}

$npgsqlCandidates = @(Get-StableVersions 'Npgsql.EntityFrameworkCore.PostgreSQL' |
    Where-Object { $_.Major -eq $efVersion.Major -and $_.Minor -eq $efVersion.Minor } |
    Sort-Object -Descending |
    Where-Object { (Get-EfLowerBound $_) -le $efVersion })
$manual = [System.Collections.Generic.List[string]]::new()
if ($npgsqlCandidates.Count -gt 0) {
    $npgsqlVersion = $npgsqlCandidates[0]
    if ($npgsqlVersion -lt $currentNpgsql) { $npgsqlVersion = $currentNpgsql }
} else {
    $npgsqlVersion = $currentNpgsql
    $manual.Add("No hay Npgsql.EntityFrameworkCore.PostgreSQL estable compatible con EF Core $efVersion; se mantiene $currentNpgsql. Revisar compatibilidad antes de publicar.")
}

$currentAbi = [regex]::Match((Read-Text $buildYaml), '(?m)^targetAbi:\s*"?([0-9.]+)').Groups[1].Value
$currentEf = [regex]::Match($pluginXml, 'Include="Microsoft\.EntityFrameworkCore\.Design"\s+Version="([^"]+)"').Groups[1].Value
$currentPlugin = [version]([regex]::Match((Read-Text $buildProps), '<Version>([^<]+)</Version>').Groups[1].Value)
$newPlugin = if ($target.Major -ne $current.Major) {
    [version]::new($currentPlugin.Major + 1, 0, 0, 0)
} elseif ($target.Minor -ne $current.Minor) {
    [version]::new($currentPlugin.Major, $currentPlugin.Minor + 1, 0, 0)
} else {
    [version]::new($currentPlugin.Major, $currentPlugin.Minor, [Math]::Max($currentPlugin.Build, 0) + 1, 0)
}
$abi = "$($target.Major).$($target.Minor).$([Math]::Max($target.Build, 0)).0"
Write-Host "EF Core $efVersion, Npgsql EF $npgsqlVersion, plugin $currentPlugin -> $newPlugin, ABI $abi"

# ── 2. Files ──────────────────────────────────────────────────────────────────────────────────────
Write-Text $pluginProject ([regex]::Replace((Read-Text $pluginProject), '<JellyfinVersion>[^<]+</JellyfinVersion>', "<JellyfinVersion>$target</JellyfinVersion>"))
foreach ($id in 'Microsoft.EntityFrameworkCore.Design', 'Microsoft.Data.Sqlite', 'Microsoft.EntityFrameworkCore.Sqlite') {
    Set-PackageVersion $pluginProject $id "$efVersion"
    Set-PackageVersion $testProject $id "$efVersion"
}
Set-PackageVersion $pluginProject 'Npgsql.EntityFrameworkCore.PostgreSQL' "$npgsqlVersion"
foreach ($id in 'Jellyfin.Database.Implementations', 'Jellyfin.Controller', 'Jellyfin.Common') {
    Set-PackageVersion $testProject $id "$target"
}

$yaml = Read-Text $buildYaml
$yaml = [regex]::Replace($yaml, '(?m)^targetAbi:.*$', "targetAbi: `"$abi`"")
$yaml = [regex]::Replace($yaml, '(?m)^version:.*$', "version: `"$newPlugin`"")
Write-Text $buildYaml $yaml

$props = Read-Text $buildProps
foreach ($tag in 'Version', 'AssemblyVersion', 'FileVersion') {
    $props = [regex]::Replace($props, "<$tag>[^<]+</$tag>", "<$tag>$newPlugin</$tag>")
}
Write-Text $buildProps $props

# ── 3. EF migration ───────────────────────────────────────────────────────────────────────────────
$toolDir = Join-Path $repoRoot '.tools'
Invoke-Dotnet @('tool', 'update', 'dotnet-ef', '--tool-path', $toolDir, '--version', "$efVersion") | Out-Null
$dotnetEf = Join-Path $toolDir 'dotnet-ef'

# dotnet-ef has to load Jellyfin's own assemblies, which the plugin normally excludes from its output.
$designBuild = @('build', $pluginProject, '-c', 'Debug', '-p:IncludeJellyfinRuntime=true', '-p:TreatWarningsAsErrors=false')
Invoke-Dotnet $designBuild | Out-Null
$efArgs = @('--project', $pluginProject, '--startup-project', $pluginProject, '--configuration', 'Debug', '--no-build')

Write-Host "> dotnet-ef migrations has-pending-model-changes"
& $dotnetEf migrations has-pending-model-changes @efArgs 2>&1 | Write-Host
$pending = $LASTEXITCODE -ne 0

$migrationName = $null
$operations = @()
if ($pending) {
    $migrationName = "Jellyfin$($target.Major)$($target.Minor)" + $(if ($target.Build -gt 0) { "$($target.Build)" } else { '' })
    Write-Host "> dotnet-ef migrations add $migrationName"
    & $dotnetEf migrations add $migrationName @efArgs --output-dir 'Data/Migrations' 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet-ef migrations add falló" }

    $migrationFile = Get-ChildItem $migrationsDir -Filter "*_$migrationName.cs" | Select-Object -First 1
    Remove-Bom $migrationFile.FullName  # Same as the existing migrations; the Designer files keep theirs.

    # dotnet-ef scaffolds array literals, which the repository's analyzers reject (CA1861); same attribute as Jellyfin121.
    $migrationText = Read-Text $migrationFile.FullName
    $suppression = '    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "EF migration operations execute once and follow the scaffolded array format.")]'
    $migrationText = [regex]::Replace($migrationText, "(?m)^(    public partial class $migrationName : Migration)", "$suppression`n`$1")
    Write-Text $migrationFile.FullName $migrationText

    $upBody = [regex]::Match((Read-Text $migrationFile.FullName), '(?s)void Up\(MigrationBuilder migrationBuilder\)\s*\{(.*?)\n        \}').Groups[1].Value
    $operations = [regex]::Matches($upBody, 'migrationBuilder\.(\w+)(?:<[^>]+>)?\(\s*(?:name:\s*"([^"]+)")?(?:,\s*(?:schema:\s*[^,]+,\s*)?table:\s*"([^"]+)")?') |
        ForEach-Object { ($_.Groups[1].Value, $_.Groups[2].Value, $(if ($_.Groups[3].Value) { "en $($_.Groups[3].Value)" })) -join ' ' -replace '\s+', ' ' }

    $unsafeKinds = 'DropColumn', 'DropTable', 'AlterColumn', 'RenameColumn', 'RenameTable', 'Sql', 'UpdateData', 'DeleteData'
    if ([regex]::Matches($upBody, 'migrationBuilder\.(\w+)') | Where-Object { $unsafeKinds -contains $_.Groups[1].Value }) {
        $manual.Add("La migración $migrationName modifica o elimina datos: revisar que la conversión de SQLite tenga equivalente en PostgreSQL (como json_remove en Jellyfin121).")
    }

    # The new migration has to be compiled before the script can include it.
    Invoke-Dotnet $designBuild | Out-Null
    Write-Host "> dotnet-ef migrations script -> Resources/schema.sql"
    & $dotnetEf migrations script @efArgs --output $schemaSql 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet-ef migrations script falló" }
    Remove-Bom $schemaSql
}

# ── 4. Build and tests ────────────────────────────────────────────────────────────────────────────
$build = Invoke-Dotnet @('build', (Join-Path $repoRoot 'Jellyfin.Database.Providers.Postgres.sln'), '-c', 'Release') -AllowFailure
$buildErrors = @($build.Output | Where-Object { $_ -match ': error ' } | ForEach-Object { ($_ -replace '^.*?/src/|\s+\[.*$', '').Trim() } | Sort-Object -Unique)

$testSummary = 'no ejecutadas'
$failedTests = @()
if ($build.ExitCode -eq 0 -and -not $SkipTests) {
    if ($ConnectionString) { $env:POSTGRES_TEST_CONNECTION = $ConnectionString }
    $resultsDir = Join-Path $repoRoot 'artifacts/test-results'
    $test = Invoke-Dotnet @('test', $testProject, '-c', 'Release', '--no-build', '--logger', 'trx;LogFileName=actualizacion.trx', '--results-directory', $resultsDir) -AllowFailure
    $testSummary = ($test.Output | Where-Object { $_ -match '(Passed|Failed)!\s+-' } | Select-Object -Last 1)
    if (-not $testSummary) { $testSummary = "código de salida $($test.ExitCode)" }
    $failedTests = @($test.Output | Where-Object { $_ -match '^\s+Failed\s+\S' } | ForEach-Object { $_.Trim() -replace '^Failed\s+', '' -replace '\s+\[.*$', '' })
    if (-not $ConnectionString) { $manual.Add('Las pruebas de integración se omitieron: no había POSTGRES_TEST_CONNECTION.') }
}

# ── 5. What changed in Jellyfin for a provider ───────────────────────────────────────────────────
function Resolve-Tag([version]$Version) {
    $candidates = @("v$($Version.Major).$($Version.Minor).$([Math]::Max($Version.Build, 0))")
    if ($Version.Build -le 0) { $candidates += "v$($Version.Major).$($Version.Minor)" }
    foreach ($tag in $candidates) {
        try {
            Invoke-RestMethod -Uri "https://api.github.com/repos/jellyfin/jellyfin/git/ref/tags/$tag" -Headers $script:ghHeaders | Out-Null
            return $tag
        } catch { }
    }
    $null
}

$ghHeaders = @{ 'User-Agent' = 'jellyfin-postgres-provider-bump'; 'Accept' = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $ghHeaders['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
$relevant = @()
$fromTag = Resolve-Tag $current
$toTag = Resolve-Tag $target
if ($fromTag -and $toTag) {
    try {
        $compare = Invoke-RestMethod -Uri "https://api.github.com/repos/jellyfin/jellyfin/compare/$fromTag...$toTag" -Headers $ghHeaders
        $relevant = @($compare.files | Where-Object {
                $_.filename -match 'IJellyfinDatabaseProvider\.cs$' -or
                $_.filename -match '^src/Jellyfin\.Database/Jellyfin\.Database\.Implementations/' -or
                $_.filename -match '^Jellyfin\.Server/Migrations/Routines/' -or
                # -and and -or have the same precedence in PowerShell: the parentheses are required.
                ($_.filename -match '^src/Jellyfin\.Database/Jellyfin\.Database\.Providers\.Sqlite/Migrations/.*\.cs$' -and $_.filename -notmatch 'Designer\.cs$|ModelSnapshot\.cs$')
            } | ForEach-Object { "$($_.status): ``$($_.filename)``" })
        if ($relevant -match 'IJellyfinDatabaseProvider\.cs') {
            $manual.Add('IJellyfinDatabaseProvider cambió: revisar los miembros nuevos (la prueba DefaultInterfaceMembersAreImplemented falla si alguno queda con la implementación por defecto).')
        }
        if ($relevant -match 'Migrations/Routines/') {
            $manual.Add('Hay migraciones de código nuevas o modificadas en Jellyfin: comprobar que su SQL funciona en PostgreSQL (ver Jellyfin121MigrationInterceptor).')
        }
    } catch {
        $manual.Add("No se pudo consultar la comparación $fromTag...$toTag en GitHub: $($_.Exception.Message)")
    }
} else {
    $manual.Add("No se encontraron las etiquetas de Jellyfin para $current o $target; revisar los cambios a mano.")
}

if ($build.ExitCode -ne 0) { $manual.Insert(0, 'La compilación en Release falla: el proveedor no compila contra la versión nueva (ver errores).') }
if ($failedTests.Count -gt 0) { $manual.Insert(0, "$($failedTests.Count) prueba(s) fallan.") }

# ── 6. Summary ────────────────────────────────────────────────────────────────────────────────────
$ok = $build.ExitCode -eq 0 -and $failedTests.Count -eq 0 -and $testSummary -ne 'no ejecutadas'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("## Soporte para Jellyfin $target ($newPlugin)")
$lines.Add('')
$lines.Add("PR generado por ``Tools/Actualizar-Jellyfin.ps1``. $(if ($ok) { 'Compila y la suite pasa; revisar los puntos de abajo antes de fusionar.' } else { '**Requiere trabajo manual antes de fusionar.**' })")
$lines.Add('')
$lines.Add('| | Antes | Después |')
$lines.Add('| --- | --- | --- |')
$lines.Add("| Jellyfin.* | $current | $target |")
$lines.Add("| targetAbi | $currentAbi | $abi |")
$lines.Add("| Plugin | $currentPlugin | $newPlugin |")
$lines.Add("| EF Core | $currentEf | $efVersion |")
$lines.Add("| Npgsql EF | $currentNpgsql | $npgsqlVersion |")
$lines.Add('')
$lines.Add('### Migración EF')
if ($migrationName) {
    $lines.Add("Nueva migración **``$migrationName``** (dotnet-ef $efVersion); ``Resources/schema.sql`` regenerado. Operaciones de ``Up``:")
    $lines.Add('')
    $operations | ForEach-Object { $lines.Add("- $_") }
} else {
    $lines.Add('El modelo de Jellyfin no cambió: no hace falta migración.')
}
$lines.Add('')
$lines.Add('### Compilación y pruebas')
$lines.Add("- Compilación Release: $(if ($build.ExitCode -eq 0) { 'correcta' } else { '**falla**' })")
$buildErrors | Select-Object -First 20 | ForEach-Object { $lines.Add("  - ``$_``") }
$lines.Add("- Pruebas: $testSummary")
$failedTests | Select-Object -First 30 | ForEach-Object { $lines.Add("  - ``$_``") }
$lines.Add('')
$lines.Add("### Cambios de Jellyfin relevantes para el proveedor ($fromTag...$toTag)")
if ($relevant.Count -gt 0) { $relevant | ForEach-Object { $lines.Add("- $_") } } else { $lines.Add('- Ninguno detectado.') }
$lines.Add('')
$lines.Add('### Pendiente de revisión manual')
if ($manual.Count -gt 0) { $manual | ForEach-Object { $lines.Add("- [ ] $_") } } else { $lines.Add('- [ ] Leer las notas de la versión de Jellyfin.') }
$lines.Add('- [ ] Añadir docs/JELLYFIN-<versión>.md y la fila de compatibilidad de los README.')
$lines.Add('- [ ] Probar el paquete en un servidor con una copia de una base real antes de publicar el release.')

New-Item -ItemType Directory -Force -Path (Split-Path $SummaryPath) | Out-Null
[System.IO.File]::WriteAllText($SummaryPath, ($lines -join "`n") + "`n", $utf8NoBom)
Write-Host "Resumen: $SummaryPath"

Write-Output-Var 'changed' 'true'
Write-Output-Var 'jellyfin_version' "$target"
Write-Output-Var 'plugin_version' "$newPlugin"
Write-Output-Var 'migration' "$migrationName"
Write-Output-Var 'ok' $(if ($ok) { 'true' } else { 'false' })
Write-Output-Var 'summary_path' $SummaryPath
