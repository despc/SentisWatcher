#requires -Version 5.1
<#
.SYNOPSIS
Configures the Watcher battle REST API, private credentials, optional local backend secrets and optional Caddy HTTPS.
.DESCRIPTION
Run on the WINDOWS TORCH HOST. Does not install/update the plugin, guess a website's framework, alter its main .env,
print credentials, download executables or kill Torch processes. -WhatIf performs validation without changing files.
HTTPS mode needs a trusted caddy.exe, a domain already pointed at this host and inbound/NAT port 80 for ACME.
The backend may have its own operating system: remote secret transfer/application integration is deliberately not guessed.
.EXAMPLE
.\setup-battle-api.ps1 -TorchRoot C:\Torch -TorchAccount 'SERVER\torch' -TorchStopped -ReserveLoopbackUrl
.EXAMPLE
.\setup-battle-api.ps1 -TorchRoot C:\Torch -TorchAccount 'SERVER\torch' -TorchStopped `
  -BackendSecretsDirectory C:\SiteSecrets\Watcher -BackendAccount 'IIS APPPOOL\Website'
.EXAMPLE
.\setup-battle-api.ps1 -TorchRoot C:\Torch -TorchAccount 'SERVER\torch' -TorchStopped -ReserveLoopbackUrl `
  -ConfigureHttps -PublicHostName watcher-api.example.org -BackendIp 203.0.113.10 `
  -CaddyExe C:\Tools\caddy.exe -DnsReady
#>
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param(
    [Parameter(Mandatory=$true)][string]$TorchRoot,
    [Parameter(Mandatory=$true)][string]$TorchAccount,
    [string]$InstancePath,
    [ValidateRange(0,65535)][int]$PublicPort = 0,
    [ValidateRange(5,1440)][int]$DelayMinutes = 15,
    [string]$KeyFile,
    [switch]$RotateKey,
    [switch]$ReserveLoopbackUrl,
    [string]$BackendSecretsDirectory,
    [string]$BackendAccount,
    [string]$PublicApiUrl,
    [switch]$ConfigureHttps,
    [string]$PublicHostName,
    [string[]]$BackendIp,
    [string]$CaddyExe,
    [ValidatePattern('^$|^[a-fA-F0-9]{64}$')][string]$CaddySha256,
    [switch]$DnsReady,
    [ValidatePattern('^[a-zA-Z0-9_.-]+$')][string]$ProxyTaskName = 'SentisWatcher-BattleProxy',
    [string]$RestartTorchService,
    [switch]$WorldSaved,
    [switch]$TorchStopped,
    [switch]$CheckOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run this script on the Windows Torch host.' }
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1 (powershell.exe), not PowerShell 7/pwsh: this script uses .NET Framework atomic file ACL APIs.' }

function FullPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.IndexOfAny([char[]]@([char]0,[char]10,[char]13,[char]34)) -ge 0) { throw 'Invalid filesystem path.' }
    $full = [IO.Path]::GetFullPath($Value)
    if ($full.StartsWith('\\')) { throw 'Use local filesystem paths, not UNC/network shares.' }
    return $full
}
function PrivatePath([string]$Value) {
    if ($Value -notmatch '^[a-zA-Z]:[\\/]') { throw 'Private paths must be absolute local paths such as C:\TorchSecrets\battles.key; never pass the key value as a parameter.' }
    $full = FullPath $Value
    if ($full -match '(?i)[\\/](wwwroot|htdocs|public|web)([\\/]|$)') { throw 'Do not put credentials or proxy state in a public/web directory.' }
    return $full
}
function Sid([string]$Account) {
    return (New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier])
}
function RequireAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'This option needs an elevated Windows PowerShell (Run as administrator).' }
}
function Native([string]$Exe, [string[]]$Arguments) {
    # PS 5.1 treats redirected native stderr as ErrorRecords, even for successful Caddy commands.
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = -1
    $output = @(& $Exe @Arguments 2>&1)
    return [pscustomobject]@{ Code=$global:LASTEXITCODE; Output=$output }
}
function LoadConfig([string]$File) {
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1048576
    $reader = [Xml.XmlReader]::Create($File, $settings)
    $document = New-Object Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.XmlResolver = $null
    try { $document.Load($reader) } finally { $reader.Dispose() }
    if ($document.DocumentElement.Name -ne 'WatcherConfig' -or $document.DocumentElement.NamespaceURI) { throw 'This is not a supported WatcherConfig XML file.' }
    return $document
}
function Token([string]$File) {
    $info = New-Object IO.FileInfo($File)
    if (!$info.Exists -or $info.Length -lt 32 -or $info.Length -gt 512) { throw 'Existing key file is missing/invalid; it will not be replaced silently. Use -RotateKey for intentional rotation.' }
    $value = [IO.File]::ReadAllText($File, [Text.Encoding]::UTF8).Trim()
    if ($value.Length -lt 32 -or $value.Length -gt 256 -or $value.ToCharArray().Where({[int]$_ -le 32 -or [int]$_ -ge 127}).Count -gt 0) { throw 'Existing key file is invalid; credential contents are not printed.' }
    return $value
}
function FileAcl([Security.Principal.SecurityIdentifier[]]$Readers) {
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($owner in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($owner, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
    }
    foreach ($reader in $Readers) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($reader, [Security.AccessControl.FileSystemRights]::Read, [Security.AccessControl.AccessControlType]::Allow)))
    }
    return $acl
}
function PrivateWrite([string]$File, [byte[]]$Bytes, [Security.Principal.SecurityIdentifier[]]$Readers) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($File))
    $acl = FileAcl $Readers
    $temporary = $File + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $stream = $null
    try {
        # Private from the first byte, not just after Set-Acl on an inherited public file.
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::Write,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $acl)
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Dispose(); $stream = $null
        if ([IO.File]::Exists($File)) {
            # File.Replace preserves destination security; secure it before replacing its contents.
            [IO.File]::SetAccessControl($File, $acl)
            [IO.File]::Replace($temporary, $File, [NullString]::Value)
        } else { [IO.File]::Move($temporary, $File) }
        [IO.File]::SetAccessControl($File, $acl)
    } finally {
        if ($stream) { $stream.Dispose() }
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
function DirectoryAccess([string]$Directory, [Security.Principal.SecurityIdentifier]$Account, [Security.AccessControl.FileSystemRights]$Rights) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($owner in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($owner, [Security.AccessControl.FileSystemRights]::FullControl,
            $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($Account, $Rights, $inherit,
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)))
    [IO.Directory]::SetAccessControl($Directory, $acl)
}
function SetElement([xml]$Xml, [string]$Name, [string]$Value) {
    $node = $Xml.DocumentElement.SelectSingleNode($Name)
    if (!$node) { $node = $Xml.CreateElement($Name); [void]$Xml.DocumentElement.AppendChild($node) }
    $node.InnerText = $Value
}
function GetElement([xml]$Xml, [string]$Name, [string]$Fallback) {
    $node = $Xml.DocumentElement.SelectSingleNode($Name)
    if ($node) { return $node.InnerText }
    return $Fallback
}
function ProxyConfigText([string]$DataDirectory, [string]$HostName, [int]$Port, [string[]]$Addresses) {
    $root = $DataDirectory.Replace('\','/')
    $lines = @('{', '  admin off', '  log default {', ('    output file "' + $root + '/caddy.log" {'),
        '      roll_size 10MiB', '      roll_keep 3', '      roll_keep_for 168h', '    }', '    level INFO', '  }',
        '  storage file_system {', ('    root "' + $root + '"'), '  }', '}',
        ($HostName.ToLowerInvariant() + ' {'), '  tls {', '    issuer acme {', '      disable_tlsalpn_challenge', '    }', '  }',
        '  @backend {', '    method GET', '    path_regexp battle "^/battles(?:/[a-f0-9]{32})?$"',
        ('    remote_ip ' + ($Addresses -join ' ')), '  }', '  handle @backend {', ('    reverse_proxy 127.0.0.1:' + $Port + ' {'),
        ('      header_up Host 127.0.0.1:' + $Port), '      header_down -Access-Control-Allow-Origin', '    }', '  }',
        '  handle {', '    respond "Forbidden" 403', '  }', '}')
    return $lines -join [Environment]::NewLine
}
function HttpCheck([int]$Port, [string]$Secret) {
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(90)
    try {
        $base = 'http://127.0.0.1:' + $Port
        $unauthorized = $client.GetAsync($base + '/battles').GetAwaiter().GetResult()
        if ([int]$unauthorized.StatusCode -ne 401) { throw ('Expected HTTP 401 without a key; got ' + [int]$unauthorized.StatusCode + '. Restart Torch with the new plugin/config first.') }
        $unauthorized.Dispose()
        $client.DefaultRequestHeaders.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Secret)
        $response = $client.GetAsync($base + '/battles?hours=24&pageSize=1').GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200) { throw ('Authorized API check returned HTTP ' + [int]$response.StatusCode + '. Credential contents are not printed.') }
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $json = ConvertFrom-Json $body
        if ($json.schemaVersion -ne 1) { throw 'Unexpected REST schema version.' }
        if ($response.Headers.Contains('Access-Control-Allow-Origin')) { throw 'Protected endpoint unexpectedly allows browser CORS.' }
        $response.Dispose()
        foreach ($path in @('/online','/perf?hours=1')) {
            $legacy = $client.GetAsync($base + $path).GetAwaiter().GetResult()
            if ([int]$legacy.StatusCode -ne 200) { throw ('Existing stats endpoint failed: ' + $path) }
            $legacy.Dispose()
        }
        Write-Host ('HTTP checks passed: no key=401, key=200; published battles: ' + $json.total + '; /online and /perf=200.')
    } finally { $client.Dispose(); $handler.Dispose() }
}

# Validate everything possible BEFORE creating secrets or modifying the instance.
$TorchRoot = FullPath $TorchRoot
if (!$InstancePath) { $InstancePath = Join-Path $TorchRoot 'Instance' }
$InstancePath = FullPath $InstancePath
$configFile = Join-Path $InstancePath 'SentisWatcher.cfg'
if (!(Test-Path -LiteralPath $configFile -PathType Leaf)) { throw 'Existing Instance\SentisWatcher.cfg is required; specify -InstancePath for a nonstandard instance.' }
$xml = LoadConfig $configFile
$torchSid = Sid $TorchAccount
if ($PublicPort -eq 0) {
    $configuredPort = GetElement $xml 'PublicPort' '0'
    if ($configuredPort -notmatch '^\d+$') { throw 'Existing PublicPort is invalid.' }
    $PublicPort = [int]$configuredPort
    if ($PublicPort -eq 0) { $PublicPort = 18953 }
}
if ($PublicPort -lt 1 -or $PublicPort -gt 65535 -or $PublicPort -eq [int](GetElement $xml 'WebPort' '18950')) { throw 'PublicPort must be valid and different from the private web-view port.' }
if (!$KeyFile) {
    $KeyFile = GetElement $xml 'PublicBattlesKeyFile' ''
    if (!$KeyFile) { $KeyFile = Join-Path $InstancePath 'SentisWatcher-Secrets\battles.key' }
}
$KeyFile = PrivatePath $KeyFile
if ($CheckOnly) {
    if ($WhatIfPreference) { Write-Host 'WhatIf: would verify the running local API; no key is read and no requests are sent.'; return }
    if ((GetElement $xml 'PublicBattlesEnabled' 'false') -ne 'true') { throw 'Battle REST is not enabled in the config.' }
    $configuredKey = GetElement $xml 'PublicBattlesKeyFile' ''
    HttpCheck $PublicPort (Token $configuredKey)
    return
}
if ($RotateKey) {
    $KeyFile = Join-Path ([IO.Path]::GetDirectoryName($KeyFile)) (([IO.Path]::GetFileNameWithoutExtension($KeyFile)) + '-' + (Get-Date -Format 'yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6) + '.key')
}
if ([IO.File]::Exists($KeyFile)) { [void](Token $KeyFile) }
$backendSid = $null
if ($BackendSecretsDirectory) {
    $BackendSecretsDirectory = PrivatePath $BackendSecretsDirectory
    if (!$BackendAccount) { throw '-BackendAccount is required when copying local backend secrets.' }
    $backendSid = Sid $BackendAccount
    if ([IO.Path]::GetDirectoryName($KeyFile).TrimEnd('\') -eq $BackendSecretsDirectory.TrimEnd('\')) { throw 'Use a separate backend secrets directory, not the Torch key directory.' }
    $existingBackendKey = Join-Path $BackendSecretsDirectory 'watcher-battles.key'
    if (!$RotateKey -and [IO.File]::Exists($existingBackendKey)) {
        if (![IO.File]::Exists($KeyFile) -or (Token $existingBackendKey) -ne (Token $KeyFile)) { throw 'The backend directory has a different key; refusing to modify either side without explicit -RotateKey.' }
    }
} elseif ($BackendAccount) { throw '-BackendAccount requires -BackendSecretsDirectory.' }
if (!$ConfigureHttps -and ($PublicHostName -or $BackendIp -or $CaddyExe -or $CaddySha256 -or $DnsReady)) { throw 'HTTPS setup parameters require -ConfigureHttps. For an existing proxy use -PublicApiUrl instead.' }
if ($PublicApiUrl) {
    $uri = $null
    if (![Uri]::TryCreate($PublicApiUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') { throw '-PublicApiUrl must be an HTTPS origin without credentials, query, fragment or path.' }
    $PublicApiUrl = $uri.GetLeftPart([UriPartial]::Authority)
}
$proxyDirectory = Join-Path $InstancePath 'SentisWatcher-BattleProxy'
$proxyConfig = Join-Path $proxyDirectory 'Caddyfile'
$proxyBinary = Join-Path $proxyDirectory 'bin\caddy.exe'
$localService = New-Object Security.Principal.SecurityIdentifier('S-1-5-19')
$existingTask = $null
if ($ConfigureHttps) {
    RequireAdmin
    if (!$DnsReady) { throw 'Set -DnsReady only after the domain points to this host and TCP 80/443 reach it (NAT/security groups included).' }
    if (!$PublicHostName -or $PublicHostName.Length -gt 253 -or $PublicHostName -notmatch '^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$') { throw 'Specify a valid public DNS hostname, without a scheme or path.' }
    if (!$BackendIp -or $BackendIp.Count -eq 0) { throw 'Explicit -BackendIp addresses are required; HTTPS will not be opened to everybody.' }
    foreach ($address in $BackendIp) {
        $parsed = $null
        if (![Net.IPAddress]::TryParse($address, [ref]$parsed) -or $address -in @('0.0.0.0','::','255.255.255.255')) { throw '-BackendIp accepts explicit IPv4/IPv6 addresses, not hostnames, wildcards or CIDRs.' }
    }
    $BackendIp = @($BackendIp | ForEach-Object { [Net.IPAddress]::Parse($_).ToString() })
    if (!$CaddyExe) { throw 'Provide a trusted caddy.exe via -CaddyExe. This script does not download or execute arbitrary installer URLs.' }
    $CaddyExe = FullPath $CaddyExe
    if (!(Test-Path -LiteralPath $CaddyExe -PathType Leaf)) { throw 'caddy.exe was not found.' }
    if ($CaddySha256 -and (Get-FileHash -LiteralPath $CaddyExe -Algorithm SHA256).Hash -ne $CaddySha256) { throw 'caddy.exe SHA256 mismatch.' }
    if ((Test-Path -LiteralPath $proxyDirectory) -and !(Test-Path -LiteralPath (Join-Path $proxyDirectory '.watcher-managed'))) { throw 'The proxy directory already exists without our ownership marker; refusing to change its ACL/config.' }
    foreach ($suffix in @('ACME','HTTPS')) {
        $rule = Get-NetFirewallRule -Name ($ProxyTaskName + '-' + $suffix) -ErrorAction SilentlyContinue
        if ($rule -and ($rule | Get-NetFirewallApplicationFilter).Program -ne $proxyBinary) { throw 'A firewall rule with this name belongs to another program; refusing to replace it.' }
    }
    $existingTask = Get-ScheduledTask -TaskName $ProxyTaskName -ErrorAction SilentlyContinue
    if ($existingTask -and (@($existingTask.Actions).Count -ne 1 -or $existingTask.Actions[0].Execute -ne $proxyBinary -or $existingTask.Actions[0].Arguments -notlike ('*' + $proxyConfig + '*'))) { throw 'A different scheduled task already uses this name; it will not be overwritten.' }
    foreach ($connection in @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in @(80,443) })) {
        $owner = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $connection.OwningProcess)
        if (!$owner -or $owner.ExecutablePath -ne $proxyBinary) { throw 'TCP 80 or 443 is already used by another application. Use its existing HTTPS proxy and -PublicApiUrl; do not replace it.' }
    }
    $PublicApiUrl = 'https://' + $PublicHostName.ToLowerInvariant()
}
if (!$PublicApiUrl) { $PublicApiUrl = 'http://127.0.0.1:' + $PublicPort }
if ($ReserveLoopbackUrl -or $RestartTorchService) { RequireAdmin }
if ($RestartTorchService) {
    if (!$WorldSaved) { throw 'Save the world first, then explicitly pass -WorldSaved before restarting a Torch service.' }
    $service = Get-Service -Name $RestartTorchService
    $serviceInfo = Get-CimInstance Win32_Service | Where-Object { $_.Name -eq $RestartTorchService }
    $serviceExecutable = ''
    if ($serviceInfo -and $serviceInfo.PathName -match '^\s*(?:"([^"]+)"|(.+?\.exe))(?:\s|$)') {
        if ($Matches[1]) { $serviceExecutable = $Matches[1] } else { $serviceExecutable = $Matches[2] }
    }
    if (!$serviceExecutable.StartsWith($TorchRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The service executable is not inside this TorchRoot; refusing to stop an unrelated service/wrapper.' }
} elseif (!$TorchStopped) { throw 'Save the world and stop this Torch instance, then pass -TorchStopped. Editing a running plugin config is unsafe: Dispose can overwrite it. Alternatively specify its exact -RestartTorchService and -WorldSaved.' }
$targetExecutable = Join-Path $TorchRoot 'Torch.Server.exe'
$runningTarget = @(Get-CimInstance Win32_Process -Filter "Name='Torch.Server.exe'" | Where-Object { $_.ExecutablePath -eq $targetExecutable })
if ($runningTarget.Count -gt 0 -and !$RestartTorchService) { throw 'This Torch instance is still running. No files were changed; save and stop it first.' }
Write-Host ('Target instance: ' + $InstancePath)
Write-Host ('Public REST origin for backend: ' + $PublicApiUrl)
Write-Host ('Publication: completed battles + ' + $DelayMinutes + ' minute delay; no private port/firewall rule will be opened.')
if (!$PSCmdlet.ShouldProcess($InstancePath, 'Configure private battle API credentials, XML and explicitly selected backend/HTTPS options')) { return }

$serviceStoppedByScript = $false
$proxyStoppedByScript = $false
try {
if ($RestartTorchService) {
    # Stop BEFORE editing: Watcher saves its in-memory config on Dispose.
    $serviceStoppedByScript = $true
    Stop-Service -Name $RestartTorchService
    (Get-Service -Name $RestartTorchService).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(90))
    if (@(Get-CimInstance Win32_Process -Filter "Name='Torch.Server.exe'" | Where-Object { $_.ExecutablePath -eq $targetExecutable }).Count -gt 0) { throw 'Service stopped but its Torch process remains alive; refusing to rewrite its config.' }
    $xml = LoadConfig $configFile
}
# The private key is never printed, placed in a native command line, or written into XML/Caddy/.env.
if (![IO.File]::Exists($KeyFile)) {
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 32
    try { $random.GetBytes($bytes); $secret = [Convert]::ToBase64String($bytes) } finally { $random.Dispose(); [Array]::Clear($bytes,0,$bytes.Length) }
    PrivateWrite $KeyFile ([Text.Encoding]::UTF8.GetBytes($secret + [Environment]::NewLine)) @($torchSid)
} else { $secret = Token $KeyFile; [IO.File]::SetAccessControl($KeyFile, (FileAcl @($torchSid))) }
$backupDirectory = Join-Path $InstancePath ('SentisWatcher-SetupBackups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
SetElement $xml 'PublicPort' ([string]$PublicPort)
SetElement $xml 'PublicBattlesEnabled' 'true'
SetElement $xml 'PublicBattlesKeyFile' $KeyFile
SetElement $xml 'PublicBattlesDelayMinutes' ([string]$DelayMinutes)
if ($xml.FirstChild -is [Xml.XmlDeclaration]) { $xml.FirstChild.Encoding = 'utf-8' }
$newXml = $xml.OuterXml
if ([IO.File]::ReadAllText($configFile) -ne $newXml) {
    [void][IO.Directory]::CreateDirectory($backupDirectory)
    Copy-Item -LiteralPath $configFile -Destination (Join-Path $backupDirectory 'SentisWatcher.cfg')
    $temporaryConfig = $configFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporaryConfig, $newXml, (New-Object Text.UTF8Encoding($false)))
        [IO.File]::Replace($temporaryConfig, $configFile, [NullString]::Value)
    } finally { if (Test-Path -LiteralPath $temporaryConfig) { Remove-Item -LiteralPath $temporaryConfig } }
    Write-Host ('Config backup: ' + (Join-Path $backupDirectory 'SentisWatcher.cfg'))
}
if ($ReserveLoopbackUrl) {
    $url = 'http://127.0.0.1:' + $PublicPort + '/'
    $existing = Native 'netsh.exe' @('http','show','urlacl',('url=' + $url))
    if ($existing.Code -eq 0 -and ($existing.Output -join ' ') -match 'D:\(') { Write-Warning 'An existing loopback URL ACL was preserved. Verify it permits the Torch service account.' }
    else {
        $result = Native 'netsh.exe' @('http','add','urlacl',('url=' + $url),('user=' + $TorchAccount),'listen=yes')
        if ($result.Code -ne 0) { throw 'Could not reserve the loopback URL. Config/key are saved; no external port was opened.' }
        Write-Host 'Loopback HTTP URL ACL reserved for Torch.'
    }
}
if ($BackendSecretsDirectory) {
    if (!(Test-Path -LiteralPath $BackendSecretsDirectory)) { DirectoryAccess $BackendSecretsDirectory $backendSid ([Security.AccessControl.FileSystemRights]::ReadAndExecute) }
    $backendKey = Join-Path $BackendSecretsDirectory 'watcher-battles.key'
    if (!$RotateKey -and [IO.File]::Exists($backendKey) -and (Token $backendKey) -ne $secret) { throw 'The backend directory already has a different key; refusing to replace it without -RotateKey.' }
    PrivateWrite $backendKey ([Text.Encoding]::UTF8.GetBytes($secret + [Environment]::NewLine)) @($backendSid)
    $envFile = Join-Path $BackendSecretsDirectory 'watcher-battles.env'
    $envText = '# Generated Watcher settings; secret value deliberately absent.' + [Environment]::NewLine +
        'WATCHER_API_URL=' + $PublicApiUrl + [Environment]::NewLine +
        'WATCHER_API_KEY_FILE="' + $backendKey.Replace('\','/') + '"' + [Environment]::NewLine
    PrivateWrite $envFile ([Text.Encoding]::UTF8.GetBytes($envText)) @($backendSid)
    Write-Host ('Backend secret and separate env fragment prepared: ' + $envFile)
    Write-Host 'Load this fragment in the backend application and restart that application; its main .env/source code was not modified.'
}
if ($ConfigureHttps) {
    DirectoryAccess $proxyDirectory $localService ([Security.AccessControl.FileSystemRights]::ReadAndExecute)
    [void][IO.Directory]::CreateDirectory((Join-Path $proxyDirectory 'bin'))
    $dataDirectory = Join-Path $proxyDirectory 'data'
    DirectoryAccess $dataDirectory $localService ([Security.AccessControl.FileSystemRights]::Modify)
    [IO.File]::WriteAllText((Join-Path $proxyDirectory '.watcher-managed'), 'setup-battle-api.ps1 v1')
    $candidate = $proxyConfig + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($candidate, (ProxyConfigText $dataDirectory $PublicHostName $PublicPort $BackendIp), (New-Object Text.UTF8Encoding($false)))
        $validation = Native $CaddyExe @('validate','--config',$candidate,'--adapter','caddyfile')
        if ($validation.Code -ne 0) { throw ('Caddy validation failed (no credential is in the config): ' + ($validation.Output -join ' ')) }
        if ($existingTask) {
            $proxyStoppedByScript = $existingTask.State -eq 'Running'
            Stop-ScheduledTask -TaskName $ProxyTaskName
            for ($i=0; $i -lt 50 -and (Get-ScheduledTask -TaskName $ProxyTaskName).State -eq 'Running'; $i++) { Start-Sleep -Milliseconds 200 }
            if ((Get-ScheduledTask -TaskName $ProxyTaskName).State -eq 'Running') { throw 'Previous Caddy task did not stop; refusing to replace its binary/config.' }
        }
        if ($CaddyExe -ne $proxyBinary) { Copy-Item -LiteralPath $CaddyExe -Destination $proxyBinary -Force }
        if ([IO.File]::Exists($proxyConfig)) {
            [void][IO.Directory]::CreateDirectory($backupDirectory)
            Copy-Item -LiteralPath $proxyConfig -Destination (Join-Path $backupDirectory 'Caddyfile')
            [IO.File]::Replace($candidate, $proxyConfig, [NullString]::Value)
        } else { [IO.File]::Move($candidate, $proxyConfig) }
    } finally { if ([IO.File]::Exists($candidate)) { [IO.File]::Delete($candidate) } }
    $action = New-ScheduledTaskAction -Execute $proxyBinary -Argument ('run --config "' + $proxyConfig + '" --adapter caddyfile') -WorkingDirectory $proxyDirectory
    $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-19' -LogonType ServiceAccount -RunLevel Limited
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $ProxyTaskName -Action $action -Principal $principal -Trigger $trigger -Settings $taskSettings -Description 'SentisWatcher battle API HTTPS proxy; managed by setup-battle-api.ps1' -Force | Out-Null
    # Rules are scoped to our task/program. Do not touch unrelated IIS/Torch/firewall configuration.
    foreach ($suffix in @('ACME','HTTPS')) { Get-NetFirewallRule -Name ($ProxyTaskName + '-' + $suffix) -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
    New-NetFirewallRule -Name ($ProxyTaskName + '-ACME') -DisplayName ($ProxyTaskName + ' ACME HTTP challenge') -Direction Inbound -Action Allow -Protocol TCP -LocalPort 80 -Program $proxyBinary -Profile Any | Out-Null
    New-NetFirewallRule -Name ($ProxyTaskName + '-HTTPS') -DisplayName ($ProxyTaskName + ' backend HTTPS') -Direction Inbound -Action Allow -Protocol TCP -LocalPort 443 -RemoteAddress $BackendIp -Program $proxyBinary -Profile Any | Out-Null
    Start-ScheduledTask -TaskName $ProxyTaskName
    $proxyStoppedByScript = $false
    Start-Sleep -Seconds 2
    if ((Get-ScheduledTask -TaskName $ProxyTaskName).State -ne 'Running') { throw 'Caddy task did not stay running. Inspect its private data\caddy.log and Task Scheduler; TLS availability has not been verified.' }
    Write-Host ('HTTPS proxy task configured: ' + $ProxyTaskName + '. TLS issuance needs working DNS/NAT/port 80; logs: ' + (Join-Path $dataDirectory 'caddy.log'))
    Write-Host 'Verify the public HTTPS origin FROM THE ALLOWED BACKEND IP; starting a task alone does not prove certificate issuance or external reachability.'
}
if ($RestartTorchService) {
    Start-Service -Name $RestartTorchService
    $serviceStoppedByScript = $false
    (Get-Service -Name $RestartTorchService).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(90))
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddMinutes(4)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { HttpCheck $PublicPort $secret; $ready=$true; break } catch { Start-Sleep -Seconds 2 }
    }
    if (!$ready) { throw 'Torch service was restarted but REST did not become ready. Check plugin version, URL ACL and server logs; no credentials are printed.' }
} else { Write-Host 'Restart the correct Torch instance after saving the world. Then run this script with -CheckOnly to verify HTTP.' }
Write-Host ('Torch key file: ' + $KeyFile)
Write-Host ('Backend origin: ' + $PublicApiUrl)
Write-Host 'Remote backend: securely import the key into its secret store. Never paste it into frontend JS, chat, URLs or logs.'
Write-Host 'The private Watcher port was not opened. Existing /online and /perf settings were preserved.'
} catch {
    $failure = $_
    if ($proxyStoppedByScript) {
        try { Start-ScheduledTask -TaskName $ProxyTaskName } catch { Write-Warning 'Could not restart the previous proxy task; inspect Task Scheduler.' }
    }
    if ($serviceStoppedByScript) {
        try { Start-Service -Name $RestartTorchService; Write-Warning 'Setup failed; the explicitly selected Torch service was started again. Review the config backup and error.' }
        catch { Write-Warning 'Setup failed and Torch could not be started again. Start the selected service manually after reviewing its logs.' }
    }
    throw $failure
} finally { $secret = $null }
