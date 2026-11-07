$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent $source
$project = [xml](Get-Content -LiteralPath (Join-Path $source 'Desktop\Desktop.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable three-part app version is required.' }
$versionFolder = Join-Path $root "artifacts\v$version"
$portable = Join-Path $versionFolder 'portable'
$exe = Get-Item -LiteralPath (Join-Path $portable 'PressReleaseWriter.exe')
if ($exe.VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw 'Build and project versions differ.' }
$release = Join-Path $versionFolder 'release'
[IO.Directory]::CreateDirectory($release) | Out-Null
$zip = Join-Path $release 'PressReleaseWriter-win-x64.zip'
$zipTemp = "$zip.$([Guid]::NewGuid().ToString('N')).tmp"

# The explicit list keeps test fixtures, local data, source, and credentials out of the package.
$entries = @('PressReleaseWriter.exe', '사용안내.md', 'notices/dotnet-license.txt', 'notices/dotnet-third-party.txt',
    'notices/nsis-copyright.txt', 'notices/pdfpig-license.txt')
foreach ($entry in $entries) {
    if (-not (Test-Path -LiteralPath (Join-Path $portable $entry) -PathType Leaf)) { throw "Missing packaged file: $entry" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $archive = [IO.Compression.ZipFile]::Open($zipTemp, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $portable $entry), $entry,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
    Move-Item -LiteralPath $zipTemp -Destination $zip -Force
}
finally { if (Test-Path -LiteralPath $zipTemp) { Remove-Item -LiteralPath $zipTemp } }

$assets = @($exe.FullName, $zip) | ForEach-Object {
    $file = Get-Item -LiteralPath $_
    [pscustomobject]@{ Name=$file.Name; Path=$file.FullName; Size=$file.Length; Sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$report = [pscustomobject]@{ Tag="v$version"; Assets=@($assets); ZipEntries=$entries }
$json = $report | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $release 'assets.json'), $json, [Text.UTF8Encoding]::new($false))
$json
