param(
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$RootProcessId,
    [Parameter(Mandatory)][DateTimeOffset]$ExpectedStartTime,
    [Parameter(Mandatory)][ValidateLength(1, 80)][string]$Label,
    [ValidateRange(5, 10)][int]$Seconds = 5,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'host-performance.json')
)

$ErrorActionPreference = 'Stop'

function Get-ProcessTree {
    param([int]$RootProcessId)
    $rows = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name
    $byParent = @{}
    foreach ($row in $rows) {
        $key = [int]$row.ParentProcessId
        if (-not $byParent.ContainsKey($key)) { $byParent[$key] = @() }
        $byParent[$key] += $row
    }
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $queue.Enqueue($RootProcessId)
    while ($queue.Count) {
        $current = $queue.Dequeue()
        if (-not $seen.Add($current)) { continue }
        if ($byParent.ContainsKey($current)) {
            foreach ($child in $byParent[$current]) {
                if ($null -ne $child) { $queue.Enqueue([int]$child.ProcessId) }
            }
        }
    }
    return @($seen | Sort-Object)
}

function Get-Identity {
    param([int]$ProcessId)
    $process = $null
    try {
        $process = [Diagnostics.Process]::GetProcessById($ProcessId)
        $handle = $process.SafeHandle
        if ($null -eq $handle -or $handle.IsClosed -or $handle.IsInvalid) { throw 'process handle unavailable' }
        $start = [DateTimeOffset]$process.StartTime
        return [pscustomobject]@{
            Process = $process
            Name = $process.ProcessName
            Pid = $process.Id
            Start = $start.ToUniversalTime()
        }
    } catch {
        if ($null -ne $process) { $process.Dispose() }
        throw
    }
}

function Same-Set {
    param([int[]]$Left, [int[]]$Right)
    return (@($Left | Sort-Object) -join ',') -eq (@($Right | Sort-Object) -join ',')
}

function Write-Record {
    param([object]$Record)
    $path = [IO.Path]::GetFullPath($OutputPath)
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -gt 1048576) { throw 'host-performance.json is missing or too large' }
    $existing = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($null -eq $existing -or $existing.schema_version -ne 1 -or $null -eq $existing.records -or $existing.max_records -lt 1 -or $existing.max_records -gt 64) {
        throw 'host-performance.json has an unsupported schema'
    }
    if (@($existing.records).Count -ge [int]$existing.max_records) { throw 'host-performance.json record limit reached' }
    $records = @($existing.records) + $Record
    $document = [ordered]@{ schema_version = 1; max_records = [int]$existing.max_records; records = $records }
    $temporary = Join-Path (Split-Path -Parent $path) ('.measure-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        [IO.File]::WriteAllText($temporary, ($document | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $path, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

$expected = $ExpectedStartTime.ToUniversalTime()
$initialProcessIds = Get-ProcessTree $RootProcessId
$identities = @()
$reasons = [System.Collections.Generic.List[string]]::new()
try {
foreach ($childProcessId in $initialProcessIds) {
    try { $identities += Get-Identity $childProcessId } catch { $reasons.Add("process-missing-at-start:$childProcessId") }
}
$root = @($identities | Where-Object { $_.Pid -eq $RootProcessId })
if ($root.Count -ne 1 -or $root[0].Start.UtcTicks -ne $expected.UtcTicks) { throw 'expected root process identity did not match' }

$logicalCpu = [int](Get-CimInstance Win32_ComputerSystem -Property NumberOfLogicalProcessors).NumberOfLogicalProcessors
$os = Get-CimInstance Win32_OperatingSystem -Property Caption, Version, BuildNumber
$machine = Get-CimInstance Win32_ComputerSystem -Property TotalPhysicalMemory
$beforeCpu = @{}
foreach ($identity in $identities) { try { $beforeCpu[$identity.Pid] = $identity.Process.TotalProcessorTime.TotalSeconds } catch { $reasons.Add("process-unreadable-at-start:$($identity.Pid)") } }

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds

$finalProcessIds = Get-ProcessTree $RootProcessId
$afterCpu = 0.0
$workingSet = [Int64]0
foreach ($identity in $identities) {
    try {
        $current = $null
        $current = [Diagnostics.Process]::GetProcessById($identity.Pid)
        $handle = $current.SafeHandle
        if ($null -eq $handle -or $handle.IsClosed -or $handle.IsInvalid) { throw 'process handle unavailable' }
        if (([DateTimeOffset]$current.StartTime).ToUniversalTime().UtcTicks -ne $identity.Start.UtcTicks) { $reasons.Add("process-reused:$($identity.Pid)"); continue }
        $afterCpu += [Math]::Max(0, $current.TotalProcessorTime.TotalSeconds - $beforeCpu[$identity.Pid])
        $workingSet += $current.WorkingSet64
    } catch { $reasons.Add("process-missing-at-end:$($identity.Pid)") }
    finally { if ($null -ne $current) { $current.Dispose() } }
}
if (-not (Same-Set $initialProcessIds $finalProcessIds)) { $reasons.Add('process-tree-changed') }
$stopwatch.Stop()

$duration = $stopwatch.Elapsed.TotalSeconds
$record = [ordered]@{
    label = $Label
    captured_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
    duration_seconds = $duration
    status = if ($reasons.Count) { 'incomplete' } else { 'complete' }
    incomplete_reasons = @($reasons)
    host = [ordered]@{ os = $os.Caption; os_version = $os.Version; os_build = $os.BuildNumber; logical_cpu_count = $logicalCpu; physical_memory_bytes = [Int64]$machine.TotalPhysicalMemory }
    processes = @($identities | ForEach-Object { [ordered]@{ name = $_.Name; pid = $_.Pid; start_time_utc = $_.Start.ToString('O') } })
    metrics = [ordered]@{ process_count = $identities.Count; cpu_seconds = [Math]::Round($afterCpu, 6); cpu_machine_percent = [Math]::Round((100 * $afterCpu / ($duration * $logicalCpu)), 4); working_set_bytes = $workingSet }
}
Write-Record $record
$record | ConvertTo-Json -Depth 8
} finally {
    foreach ($identity in $identities) { $identity.Process.Dispose() }
}
