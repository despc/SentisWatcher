# Offline analysis using the same detector as /api/battles. Never open production with WatcherStore:
# its writer has retention cleanup. Supply a COPY in a disposable folder containing only watcher day files.
param(
    [Parameter(Mandatory=$true)][string]$CopyFolder,
    [Parameter(Mandatory=$true)][long]$From,
    [Parameter(Mandatory=$true)][long]$To,
    [string]$Output = 'battles.json',
    [string]$Battle = '',
    [int]$Gap = 120,
    [int]$Radius = 5000
)
$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot '..\SentisWatcher.Tests\bin\Debug'
$assembly = Join-Path $bin 'SentisWatcher.dll'
if (!(Test-Path $assembly)) { throw 'Build SentisWatcher.Tests first (dotnet build SentisWatcher.sln).' }
[void][Reflection.Assembly]::LoadFrom((Resolve-Path (Join-Path $bin 'Newtonsoft.Json.dll')))
[void][Reflection.Assembly]::LoadFrom((Resolve-Path $assembly))
# C# delegate: the writer invokes this on its own thread, which has no PowerShell runspace.
Add-Type -TypeDefinition 'public static class BattleRetention { public static System.Func<int> Keep = () => 100000; }'
$store = New-Object SentisWatcher.Storage.WatcherStore((Resolve-Path $CopyFolder).Path, [BattleRetention]::Keep)
try {
    $data = New-Object SentisWatcher.Storage.WebData($store)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $result = $data.Battles($From, $To, $Gap, $Radius, $Battle)
    $json = [Newtonsoft.Json.JsonConvert]::SerializeObject($result)
    [IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output), $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host ('Analysis: {0} ms; output: {1}' -f $timer.ElapsedMilliseconds, $Output)
} finally { $store.Dispose() }
