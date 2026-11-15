# Windows PowerShell 5.1. Generates a server-to-server secret without printing it or putting it in arguments.
# New file only; its restrictive ACL is applied atomically at creation. Never put this file in a web root.
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [string]$ServiceAccount = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)
$ErrorActionPreference = 'Stop'
$full = [IO.Path]::GetFullPath($Path)
if ($full.StartsWith('\\')) { throw 'Use a local private file, not a network share.' }
$parent = [IO.Path]::GetDirectoryName($full)
[void][IO.Directory]::CreateDirectory($parent)
$creator = [Security.Principal.WindowsIdentity]::GetCurrent().User
$service = (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier])
$system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
$admins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$acl = New-Object Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($creator, $system, $admins)) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
}
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($service, [Security.AccessControl.FileSystemRights]::Read, [Security.AccessControl.AccessControlType]::Allow)))
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$stream = $null
try {
    $bytes = New-Object byte[] 32
    $random.GetBytes($bytes)
    $text = [Convert]::ToBase64String($bytes) + [Environment]::NewLine
    # CreateNew refuses to replace an existing key. The FileSecurity overload avoids an inherited-ACL exposure window.
    $stream = [IO.FileStream]::new($full, [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $acl)
    $encoded = (New-Object Text.UTF8Encoding($false)).GetBytes($text)
    $stream.Write($encoded, 0, $encoded.Length)
    [Array]::Clear($bytes, 0, $bytes.Length)
    [Array]::Clear($encoded, 0, $encoded.Length)
} finally {
    if ($stream) { $stream.Dispose() }
    $random.Dispose()
}
Write-Host "Created private API key file: $full"
Write-Host 'Set PublicBattlesKeyFile to this absolute path, enable PublicBattlesEnabled, and restart Torch.'
Write-Host 'Transfer the secret to the website BACKEND secret store securely; never expose it to browser JavaScript.'
