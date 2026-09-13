$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$windows = Split-Path -Parent $PSCommandPath
$stage = Join-Path $windows 'stage'
$dist = Join-Path $windows 'dist'
$manifestPath = Join-Path $windows 'bundle-manifest.json'

py -3 -B (Join-Path $windows 'package.py')
if ($LASTEXITCODE) { throw "package.py failed with exit code $LASTEXITCODE" }

$distPath = [IO.Path]::GetFullPath($dist)
$windowsPath = [IO.Path]::GetFullPath($windows) + [IO.Path]::DirectorySeparatorChar
if (-not $distPath.StartsWith($windowsPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to replace a distribution outside desktop/windows'
}
if (Test-Path -LiteralPath $distPath) { Remove-Item -LiteralPath $distPath -Recurse -Force }

dotnet publish (Join-Path $windows 'AgentFoundry.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $distPath
if ($LASTEXITCODE) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$expected = @($manifest.files.PSObject.Properties.Name | Sort-Object)
foreach ($relative in $expected) {
    $source = Join-Path $stage ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $destination = Join-Path $distPath ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}

$actual = @(Get-ChildItem (Join-Path $distPath 'app'), (Join-Path $distPath 'runtime'), (Join-Path $distPath 'terminal') -File -Recurse |
    ForEach-Object { [IO.Path]::GetRelativePath($distPath, $_.FullName).Replace('\', '/') } | Sort-Object)
if (Compare-Object $expected $actual) { throw 'Distribution payload does not exactly match the manifest' }
foreach ($relative in $expected) {
    $expectedHash = $manifest.files.$relative
    foreach ($root in $stage, $distPath) {
        $path = Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($actualHash -ne $expectedHash) { throw "SHA-256 mismatch: $path" }
    }
}

Write-Output "Manifest SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant())"
Write-Output "Agentopia.exe SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $distPath 'Agentopia.exe')).Hash.ToLowerInvariant())"
Write-Output "Agentopia.dll SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $distPath 'Agentopia.dll')).Hash.ToLowerInvariant())"
