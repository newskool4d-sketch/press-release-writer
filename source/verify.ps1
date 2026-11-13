param(
    [string]$DotnetPath = 'dotnet',
    [switch]$ProbeCodex,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$resolvedDotnet = if (Test-Path -LiteralPath $DotnetPath) { (Resolve-Path -LiteralPath $DotnetPath).Path } else { $DotnetPath }
$values = @{
    DOTNET_CLI_HOME = (Join-Path $root '.tools\cli-home')
    NUGET_PACKAGES = (Join-Path $root '.tools\packages')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
}
$previous = @{}
foreach ($key in $values.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process')
}
function Invoke-Dotnet([string[]]$Arguments) {
    & $resolvedDotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $PSScriptRoot
try {
    Invoke-Dotnet -Arguments @('restore', 'DesktopSmoke/DesktopSmoke.csproj', '--verbosity', 'minimal')
    Invoke-Dotnet -Arguments @('restore', 'Smoke/Smoke.csproj', '--verbosity', 'minimal')
    Invoke-Dotnet -Arguments @('build', 'DesktopSmoke/DesktopSmoke.csproj', '--no-restore', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false', '--verbosity', 'minimal')
    Invoke-Dotnet -Arguments @('build', 'Smoke/Smoke.csproj', '--no-restore', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false', '--verbosity', 'minimal')
    Invoke-Dotnet -Arguments @('Smoke/bin/Debug/net10.0/Smoke.dll')
    Invoke-Dotnet -Arguments @('DesktopSmoke/bin/Debug/net10.0-windows/DesktopSmoke.dll')
    if ($ProbeCodex) { Invoke-Dotnet -Arguments @('Smoke/bin/Debug/net10.0/Smoke.dll', '--probe-codex') }
    if ($Publish) {
        $project = [xml](Get-Content -LiteralPath 'Desktop/Desktop.csproj' -Raw)
        $version = $project.Project.PropertyGroup.Version
        $output = Join-Path $root "artifacts\v$version\portable"
        Invoke-Dotnet -Arguments @('publish', 'Desktop/Desktop.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '--disable-build-servers', '-p:UseSharedCompilation=false', '-o', $output, '--verbosity', 'minimal')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'notices') -Destination $output -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $root '사용안내.html') -Destination (Join-Path $output '사용안내.html') -Force
        Copy-Item -LiteralPath (Join-Path $root '사용안내.md') -Destination (Join-Path $output '사용안내.md') -Force
    }
}
finally {
    Pop-Location
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
