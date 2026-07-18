[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AdvisoryPath,
    [Parameter(Mandatory)] [string] $SnapshotPath
)

$ErrorActionPreference = 'Stop'

function Fail([int] $Code, [string] $Message) {
    [Console]::Error.WriteLine($Message)
    exit $Code
}

function Read-JsonFile([string] $Path, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail 3 "$Label file does not exist." }
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
    if ($bytes.Length -lt 1 -or $bytes.Length -gt 1MB) { Fail 3 "$Label must be between 1 byte and 1 MiB." }
    try {
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        return $text | ConvertFrom-Json
    } catch { Fail 3 "Malformed $Label JSON: $($_.Exception.Message)" }
}

function Require-Object($Value, [string] $Label) {
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) { Fail 3 "$Label must be an object." }
}

function Require-Properties($Value, [string[]] $Names, [string] $Label) {
    Require-Object $Value $Label
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expected = @($Names | Sort-Object)
    if ((Compare-Object $actual $expected)) { Fail 3 "$Label has missing or unsupported fields." }
}

function Require-String($Value, [string] $Label) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) { Fail 3 "$Label must be a non-empty string." }
    return $Value
}

function Require-Int($Value, [int] $Minimum, [int] $Maximum, [string] $Label) {
    if ($Value -isnot [int] -and $Value -isnot [long]) { Fail 3 "$Label must be an integer." }
    if ($Value -lt $Minimum -or $Value -gt $Maximum) { Fail 3 "$Label must be between $Minimum and $Maximum." }
    return [int] $Value
}

function Read-Utc($Value, [string] $Label) {
    try {
        if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
        if ($Value -is [DateTime]) { return [DateTimeOffset]$Value.ToUniversalTime() }
        return [DateTimeOffset]::Parse((Require-String $Value $Label), [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    }
    catch { Fail 3 "$Label must be an ISO-8601 timestamp." }
}

function Identity-Key($Value, [string] $Label) {
    Require-Properties $Value @('pid', 'startTicks') $Label
    $processId = Require-Int $Value.pid 1 ([int]::MaxValue) "$Label.pid"
    if ($Value.startTicks -isnot [int] -and $Value.startTicks -isnot [long]) { Fail 3 "$Label.startTicks must be an integer." }
    if ($Value.startTicks -le 0) { Fail 3 "$Label.startTicks must be positive." }
    return "${processId}:$($Value.startTicks)"
}

$advisory = Read-JsonFile $AdvisoryPath 'advisory'
$snapshot = Read-JsonFile $SnapshotPath 'snapshot'
Require-Properties $advisory @('schemaVersion', 'advisoryId', 'snapshotHash', 'snapshotGeneratedAtUtc', 'createdAtUtc', 'expiresAtUtc', 'recommendations') 'advisory'
if ((Require-Int $advisory.schemaVersion 1 1 'advisory.schemaVersion') -ne 1) { Fail 3 'Unsupported advisory schema version.' }
$advisoryId = Require-String $advisory.advisoryId 'advisory.advisoryId'
if ($advisoryId -notmatch '^[A-Za-z0-9._-]{1,80}$') { Fail 3 'advisoryId contains unsupported characters.' }
$hash = Require-String $advisory.snapshotHash 'advisory.snapshotHash'
if ($hash -notmatch '^[a-f0-9]{64}$') { Fail 3 'snapshotHash must be lowercase SHA-256 hex.' }

Require-Properties $snapshot @('schemaVersion', 'snapshotHash', 'generatedAtUtc', 'window', 'freshness', 'coverage', 'groups', 'recommendations') 'snapshot'
if ((Require-Int $snapshot.schemaVersion 1 1 'snapshot.schemaVersion') -ne 1 -or $snapshot.snapshotHash -ne $hash) { Fail 4 'Advisory does not match the supplied version-1 snapshot.' }
Require-Properties $snapshot.freshness @('latestSampleAtUtc', 'ageSeconds', 'isFresh') 'snapshot.freshness'
if ($snapshot.freshness.isFresh -isnot [bool] -or -not $snapshot.freshness.isFresh) { Fail 4 'Snapshot telemetry is stale.' }

$now = [DateTimeOffset]::UtcNow
$snapshotGenerated = Read-Utc $snapshot.generatedAtUtc 'snapshot.generatedAtUtc'
$advisorySnapshotGenerated = Read-Utc $advisory.snapshotGeneratedAtUtc 'advisory.snapshotGeneratedAtUtc'
$created = Read-Utc $advisory.createdAtUtc 'advisory.createdAtUtc'
$expires = Read-Utc $advisory.expiresAtUtc 'advisory.expiresAtUtc'
if ($advisorySnapshotGenerated -ne $snapshotGenerated -or $snapshotGenerated -gt $now.AddMinutes(1) -or ($now - $snapshotGenerated).TotalMinutes -gt 5) { Fail 4 'Snapshot timestamp is stale, future-dated, or altered.' }
if ($created -gt $now.AddMinutes(1) -or $created -lt $snapshotGenerated.AddMinutes(-1) -or ($now - $created).TotalMinutes -gt 5 -or $expires -le $now -or $expires -gt $now.AddHours(1)) { Fail 4 'Advisory timestamps or expiry are invalid.' }
if ($advisory.recommendations -isnot [System.Collections.IEnumerable] -or @($advisory.recommendations).Count -lt 1 -or @($advisory.recommendations).Count -gt 100) { Fail 3 'recommendations must contain 1 to 100 entries.' }

$groups = @{}
foreach ($group in @($snapshot.groups)) {
    Require-Properties $group @('groupKey', 'label', 'root', 'members', 'ownerResolved', 'metrics', 'lastActivityAtUtc', 'safety') 'snapshot group'
    $key = Identity-Key $group.root 'snapshot group.root'
    Require-Properties $group.safety @('isHardBlocked', 'risk', 'flags') 'snapshot group.safety'
    $groups[$key] = $group
}

$seen = @{}
foreach ($recommendation in @($advisory.recommendations)) {
    Require-Properties $recommendation @('root', 'members', 'action', 'confidencePct', 'evidence') 'recommendation'
    $rootKey = Identity-Key $recommendation.root 'recommendation.root'
    if ($seen.ContainsKey($rootKey)) { Fail 3 'Duplicate recommendation root.' }; $seen[$rootKey] = $true
    if (-not $groups.ContainsKey($rootKey)) { Fail 5 'Recommendation root is absent or changed.' }
    $group = $groups[$rootKey]
    if ($group.ownerResolved -isnot [bool] -or -not $group.ownerResolved -or $group.safety.isHardBlocked -ne $false) { Fail 5 'Target group is unresolved or safety-blocked.' }
    if ((Require-String $recommendation.action 'recommendation.action') -ne 'investigate') { Fail 5 'Agent advisories support only investigate.' }
    if ((Require-Int $recommendation.confidencePct 0 70 'recommendation.confidencePct') -gt 70) { Fail 3 'Agent confidence cannot exceed 70%.' }
    $submitted = @($recommendation.members | ForEach-Object { Identity-Key $_ 'recommendation.member' } | Sort-Object)
    $current = @($group.members | ForEach-Object { Identity-Key $_ 'snapshot member' } | Sort-Object)
    if ($submitted.Count -lt 1 -or $submitted.Count -gt 256 -or (Compare-Object $submitted $current)) { Fail 5 'Recommendation members do not exactly match the group.' }
    $evidence = @($recommendation.evidence)
    if ($evidence.Count -lt 1 -or $evidence.Count -gt 16) { Fail 3 'Evidence must contain 1 to 16 entries.' }
    foreach ($item in $evidence) {
        Require-Properties $item @('code', 'detail') 'evidence'
        $code = Require-String $item.code 'evidence.code'; $detail = Require-String $item.detail 'evidence.detail'
        if ($code -notmatch '^[A-Za-z0-9._-]{1,64}$' -or $detail.Length -gt 500 -or $detail -match '[\x00-\x1F\x7F]' -or $detail -match '(?ix)(?:[a-z]:[\\/]|\\\\|/(?:home|users)/|--?(?:token|api[-_]?key|password|secret)\b|bearer\s+\S+|\b[A-Z_][A-Z0-9_]{2,}=)') { Fail 3 'Evidence contains unsafe or unsupported content.' }
    }
}

[Console]::WriteLine("Valid ProcLens v1 advisory: $($advisory.recommendations.Count) recommendation(s), advisoryId=$advisoryId")
