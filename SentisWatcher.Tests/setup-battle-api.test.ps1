# Windows PowerShell 5.1; no Pester, elevation, real Torch, firewall or scheduled-task changes needed.
# All credentials used here are disposable and are never printed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$setup = Join-Path $PSScriptRoot '..\tools\setup-battle-api.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('watcher-setup-test-' + [Guid]::NewGuid().ToString('N'))
$instance = Join-Path $root 'Instance'
[void][IO.Directory]::CreateDirectory($instance)
$config = Join-Path $instance 'SentisWatcher.cfg'
$account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
function Assert($Condition,[string]$Message) { if (!$Condition) { throw ('FAIL: ' + $Message) } }
function Xml { return [xml][IO.File]::ReadAllText($config) }
function Reject([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert $failed $Message
}
$arguments = @{TorchRoot=$root; TorchAccount=$account; TorchStopped=$true}
try {
    [IO.File]::WriteAllText($config, '<?xml version="1.0" encoding="utf-8"?><WatcherConfig><!--keep--><PublicPort>19123</PublicPort><PublicOrigin>https://website.invalid</PublicOrigin><WebPort>19124</WebPort><Enabled>false</Enabled></WatcherConfig>')
    $original = [IO.File]::ReadAllText($config)
    & $setup @arguments -WhatIf
    Assert ([IO.File]::ReadAllText($config) -eq $original) 'WhatIf modified config'
    Assert (!(Test-Path (Join-Path $instance 'SentisWatcher-Secrets'))) 'WhatIf created secrets'
    Write-Host 'PASS: dry run has no file changes'

    Reject { & $setup -TorchRoot $root -TorchAccount $account } 'running/stopped confirmation must be required'
    Reject { & $setup @arguments -PublicPort 19124 } 'private/public port collision must fail'
    Assert ([IO.File]::ReadAllText($config) -eq $original) 'preflight failure changed config'
    Write-Host 'PASS: explicit offline confirmation and port validation'

    & $setup @arguments -BackendSecretsDirectory (Join-Path $root 'BackendSecrets') -BackendAccount $account
    $xml = Xml
    $keyFile = [string]$xml.WatcherConfig.PublicBattlesKeyFile
    $firstKey = [IO.File]::ReadAllText($keyFile).Trim()
    Assert ($firstKey.Length -eq 44) 'generated key entropy/encoding'
    Assert ((Get-Acl -LiteralPath $keyFile).AreAccessRulesProtected) 'key ACL is inherited'
    Assert ($xml.WatcherConfig.PublicPort -eq '19123') 'existing public port not preserved'
    Assert ($xml.WatcherConfig.PublicOrigin -eq 'https://website.invalid') 'existing stats origin changed'
    Assert ($xml.WatcherConfig.Enabled -eq 'false') 'unrelated configuration changed'
    Assert ($xml.OuterXml.Contains('<!--keep-->')) 'XML comment lost'
    Assert ($xml.WatcherConfig.PublicBattlesEnabled -eq 'true') 'API not enabled'
    Assert ($xml.WatcherConfig.PublicBattlesDelayMinutes -eq '15') 'wrong delay'
    Assert (!$xml.OuterXml.Contains($firstKey)) 'secret leaked into config'
    $env = [IO.File]::ReadAllText((Join-Path $root 'BackendSecrets\watcher-battles.env'))
    Assert (!$env.Contains($firstKey)) 'secret leaked into env fragment'
    Assert ($env.Contains('WATCHER_API_KEY_FILE=')) 'backend env fragment missing'
    $copied = [IO.File]::ReadAllText((Join-Path $root 'BackendSecrets\watcher-battles.key')).Trim()
    Assert ($copied -eq $firstKey) 'backend secret differs'
    Write-Host 'PASS: private key, backup, XML preservation, local backend preparation'

    & $setup @arguments
    Assert ([IO.File]::ReadAllText($keyFile).Trim() -eq $firstKey) 'idempotent rerun changed key'
    $beforeFailure = [IO.File]::ReadAllText($config)
    Reject { & $setup @arguments -PublicHostName api.example.org } 'HTTPS flags without HTTPS mode must fail'
    Reject { & $setup @arguments -PublicApiUrl 'http://remote.invalid' } 'unencrypted remote origin must fail'
    Reject { & $setup @arguments -KeyFile (Join-Path $root 'wwwroot\bad.key') } 'web-root secrets must fail'
    Assert ([IO.File]::ReadAllText($config) -eq $beforeFailure) 'validation modified config'
    Write-Host 'PASS: key reuse and unsafe options rejected before changes'

    & $setup @arguments -RotateKey -BackendSecretsDirectory (Join-Path $root 'BackendSecrets') -BackendAccount $account
    $rotatedFile = [string](Xml).WatcherConfig.PublicBattlesKeyFile
    $rotated = [IO.File]::ReadAllText($rotatedFile).Trim()
    Assert ($rotated -ne $firstKey) 'rotation did not change key'
    Assert ([IO.File]::Exists($keyFile)) 'previous key deleted during rotation'
    & $setup @arguments
    Assert ((Xml).WatcherConfig.PublicBattlesKeyFile -eq $rotatedFile) 'rerun reverted to pre-rotation key'
    Write-Host 'PASS: explicit rotation preserves old file and reruns keep the rotated key'

    $other = Join-Path $root 'ConflictingBackend'
    [void][IO.Directory]::CreateDirectory($other)
    [IO.File]::WriteAllText((Join-Path $other 'watcher-battles.key'), 'disposable-conflicting-test-key-1234567890')
    $beforeFailure = [IO.File]::ReadAllText($config)
    Reject { & $setup @arguments -BackendSecretsDirectory $other -BackendAccount $account } 'conflicting backend secret must fail'
    Assert ([IO.File]::ReadAllText($config) -eq $beforeFailure) 'backend conflict modified config'
    Write-Host 'PASS: backend secret conflict fails before changing Torch'

    # Actual local HTTP smoke check through .NET HttpClient (a disposable Node socket server, not Torch).
    $server = Join-Path $root 'mock.js'
    $portFile = Join-Path $root 'port.txt'
    [IO.File]::WriteAllText($server, @'
const fs=require('node:fs'),http=require('node:http');
const root=process.argv[2],key=fs.readFileSync(process.argv[3],'utf8').trim();
const server=http.createServer((req,res)=>{
 res.setHeader('Content-Type','application/json');
 if(req.url.startsWith('/battles')&&req.headers.authorization!=='Bearer '+key){res.writeHead(401);res.end('{}');return;}
 res.end(JSON.stringify({schemaVersion:1,total:0}));
});
server.listen(0,'127.0.0.1',()=>fs.writeFileSync(root+'/port.txt',String(server.address().port)));
'@)
    $node = (Get-Command node.exe).Source
    $process = Start-Process -FilePath $node -ArgumentList ('"' + $server + '" "' + $root + '" "' + $rotatedFile + '"') -PassThru -WindowStyle Hidden
    try {
        for ($i=0; $i -lt 50 -and !(Test-Path $portFile); $i++) { Start-Sleep -Milliseconds 100 }
        Assert (Test-Path $portFile) 'mock HTTP server did not start'
        $mockPort = [int][IO.File]::ReadAllText($portFile)
        & $setup -TorchRoot $root -TorchAccount $account -PublicPort $mockPort -CheckOnly
        Write-Host 'PASS: CheckOnly authenticates over HTTP and checks legacy endpoints'
    } finally { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() } }
    $parseErrors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path $setup),[ref]$null,[ref]$parseErrors)
    Assert ($parseErrors.Count -eq 0) 'setup parser errors'
    foreach ($name in @('ProxyConfigText','Native')) {
        $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $proxyText=ProxyConfigText (Join-Path $root 'ProxyData') 'api.example.org' 19123 @('203.0.113.10','2001:db8::1')
    Assert ($proxyText.Contains('remote_ip 203.0.113.10 2001:db8::1')) 'proxy source IP restriction missing'
    Assert ($proxyText.Contains('header_up Host 127.0.0.1:19123')) 'loopback HttpListener Host not set'
    Assert ($proxyText.Contains('admin off')) 'Caddy admin API not disabled'
    Assert ($proxyText.Contains('^/battles(?:/[a-f0-9]{32})?$')) 'proxy route whitelist missing'
    Assert (!$proxyText.Contains($rotated)) 'credential leaked into proxy config'
    if ($env:WATCHER_TEST_CADDY) {
        $candidate=Join-Path $root 'Caddyfile'
        [IO.File]::WriteAllText($candidate,$proxyText)
        $validation=Native $env:WATCHER_TEST_CADDY @('validate','--config',$candidate,'--adapter','caddyfile')
        Assert ($validation.Code -eq 0) ('Caddy validation: ' + ($validation.Output -join ' '))
        Write-Host 'PASS: generated HTTPS configuration accepted by real Caddy (no listener/task/firewall changes)'
    }
    Write-Host 'PASS: proxy whitelist, backend IPs, Host and credential exclusion'
    Write-Host 'ALL 8 SETUP TEST GROUPS PASSED'
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
