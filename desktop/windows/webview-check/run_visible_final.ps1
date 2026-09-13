$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$checkDir = Join-Path $repo 'desktop\windows\webview-check'
$publish = Join-Path $checkDir 'bin\Release\net8.0-windows\win-x64\publish'
$prefix = Join-Path $checkDir 'visible-08'
if (Test-Path "$prefix-execution.json") { throw 'Final visible attempt already recorded; do not repeat.' }
if ((Test-Path "$prefix.stdout.json") -or (Test-Path "$prefix.stderr.txt")) { throw 'Final visible output already exists; do not repeat.' }
$manifestPath = Join-Path $repo 'desktop\windows\bundle-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$payload = foreach ($file in $manifest.files.PSObject.Properties) {
    $source = Join-Path "$repo\desktop\windows\stage" $file.Name
    $destination = Join-Path $publish $file.Name
    if ((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $file.Value) { throw "Stage hash mismatch: $($file.Name)" }
    if ($file.Name.StartsWith('app/')) {
        if ((Get-FileHash -LiteralPath (Join-Path $repo $file.Name.Substring(4))).Hash.ToLowerInvariant() -ne $file.Value) { throw "Source hash mismatch: $($file.Name)" }
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $hash = (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant()
    if ($hash -ne $file.Value) { throw "Checker payload mismatch: $($file.Name)" }
    [ordered]@{ path = $file.Name; sha256 = $hash }
}
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $publish 'webview-check.dll'))
$resource = $assembly.GetManifestResourceStream('AgentFoundry.bundle.json')
try { $embeddedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($resource)).ToLowerInvariant() }
finally { $resource.Dispose() }
$manifestHash = (Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant()
if ($embeddedHash -ne $manifestHash) { throw 'Embedded checker manifest differs from current manifest.' }
$hashes = [ordered]@{
    checked_at = [DateTimeOffset]::UtcNow.ToString('O'); manifest_sha256 = $manifestHash
    embedded_manifest_sha256 = $embeddedHash; payload = @($payload)
    files = @(Get-FileHash -LiteralPath @("$publish\webview-check.exe", "$publish\webview-check.dll", "$checkDir\VisibleCheck.cs", "$checkDir\test_visible_probe.cjs", "$repo\observatory.html", "$repo\desktop\windows\dist\AgentFoundry.exe", "$repo\desktop\windows\dist\AgentFoundry.dll") | Select-Object Path,Hash)
}
$hashes | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$prefix-hashes.json"
$ports = @(8777,8778,8779,8589,53931,41768,46897,21057,44705,1216,1217,13790,13791)
function RelevantProcesses {
    $all = @(Get-CimInstance Win32_Process)
    $matches = @($all | Where-Object {
        $_.Name -match '^(AgentFoundry|webview-check)\.exe$' -or
        ($_.Name -match '^(pythonw?|msedgewebview2|conhost)\.exe$' -and $_.CommandLine -match 'agent-session-monitor|agent-foundry-webview-check-|AgentFoundry\\WebView')
    })
    $ids = @($matches.ProcessId)
    do {
        $children = @($all | Where-Object { $_.ParentProcessId -in $ids -and $_.ProcessId -notin $ids })
        $matches += $children; $ids += @($children.ProcessId)
    } while ($children.Count)
    @($matches | Select-Object ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath)
}
$tempRoot = [IO.Path]::GetTempPath()
$cacheRoot = Join-Path $env:LOCALAPPDATA 'AgentFoundry\WebView'
$fixturesBefore = @(Get-ChildItem -LiteralPath $tempRoot -Directory -Filter 'agent-foundry-webview-check-*' | Select-Object -ExpandProperty FullName)
$cachesBefore = @(Get-ChildItem -LiteralPath $cacheRoot -Directory | Select-Object -ExpandProperty FullName)
$preflight = [ordered]@{
    checked_at = [DateTimeOffset]::UtcNow.ToString('O'); processes = @(RelevantProcesses)
    listeners = @(Get-NetTCPConnection -State Listen | Where-Object LocalPort -in $ports | Select-Object LocalAddress,LocalPort,OwningProcess)
    checked_ports = $ports; fixtures_before = $fixturesBefore; caches_before = $cachesBefore
}
$preflight | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$prefix-preflight.json"
if ($preflight.processes.Count -or $preflight.listeners.Count) { throw 'Preflight is not clear; final visible attempt not launched.' }
$started = [DateTimeOffset]::UtcNow
$clock = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath "$publish\webview-check.exe" -ArgumentList '--visible-observatory' -WindowStyle Hidden -PassThru -RedirectStandardOutput "$prefix.stdout.json" -RedirectStandardError "$prefix.stderr.txt"
$timedOut = $false
try {
    if (-not $process.WaitForExit(60000)) {
        $timedOut = $true
        $process.Kill($true)
        $process.WaitForExit(5000) | Out-Null
    }
    $clock.Stop()
    $execution = [ordered]@{
        started_at_utc = $started.ToString('O'); pid = $process.Id; exit_code = $process.ExitCode
        elapsed_seconds = $clock.Elapsed.TotalSeconds; hard_cap_seconds = 60; timed_out = $timedOut
        mode = 'visible-observatory'; synthetic_agents = 24; synthetic_projects = 4; settle_seconds = 5; sample_seconds = 10
        terminal_enabled = $false; real_session_actions = 0; automatic_retry = $false
    }
    $execution | ConvertTo-Json | Set-Content -LiteralPath "$prefix-execution.json"
} finally { $process.Dispose() }
$stderr = Get-Content -LiteralPath "$prefix.stderr.txt" -Raw
$privatePorts = @([regex]::Matches($stderr, 'http://127\.0\.0\.1:(\d+)') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
$ports = @($ports + $privatePorts | Sort-Object -Unique)
$identities = @()
$measurement = $null
if ((Get-Item -LiteralPath "$prefix.stdout.json").Length) {
    $measurement = Get-Content -LiteralPath "$prefix.stdout.json" -Raw | ConvertFrom-Json
    $identities = @($measurement.processes.pid)
}
$cleanupLine = @(Get-Content -LiteralPath "$prefix.stderr.txt" | Where-Object { $_.StartsWith('VISIBLE_CLEANUP_RESULT: ') })
$cleanup = if ($cleanupLine.Count) { $cleanupLine[-1].Substring('VISIBLE_CLEANUP_RESULT: '.Length) | ConvertFrom-Json } else { $null }
$postflight = [ordered]@{
    checked_at = [DateTimeOffset]::UtcNow.ToString('O'); processes = @(RelevantProcesses)
    retained_numeric_pids_still_present = @(Get-Process | Where-Object Id -in $identities | Select-Object Id,ProcessName,StartTime)
    listeners = @(Get-NetTCPConnection -State Listen | Where-Object LocalPort -in $ports | Select-Object LocalAddress,LocalPort,OwningProcess)
    checked_ports = $ports; private_ports = $privatePorts
    new_fixture_directories = @(Get-ChildItem -LiteralPath $tempRoot -Directory -Filter 'agent-foundry-webview-check-*' | Where-Object FullName -notin $fixturesBefore | Select-Object -ExpandProperty FullName)
    new_cache_directories = @(Get-ChildItem -LiteralPath $cacheRoot -Directory | Where-Object FullName -notin $cachesBefore | Select-Object -ExpandProperty FullName)
    remaining_older_cache_ids = @(Get-ChildItem -LiteralPath $cacheRoot -Directory | Select-Object -ExpandProperty Name)
    cleanup = $cleanup; visible08_exit_code = $execution.exit_code
    active_measurement_valid = $measurement.active_measurement_valid; automatic_retry = $false
}
$postflight | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$prefix-postflight.json"
$execution | ConvertTo-Json
$postflight | ConvertTo-Json -Depth 8
