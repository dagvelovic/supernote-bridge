# sn-backup.ps1 — headless Supernote backup via SupernoteBridge
# Usage: powershell -ExecutionPolicy Bypass -File sn-backup.ps1 [-Target <dir>] [-Exe <path>]
param(
    [string]$Target = "$PSScriptRoot\..\BACKUP",
    [string]$Exe = "$PSScriptRoot\..\publish\SupernoteBridge.exe",
    [string]$LogDir = "$PSScriptRoot\logs"
)

New-Item -ItemType Directory -Force $LogDir | Out-Null
$log = Join-Path $LogDir ("sn-backup_{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

"[{0:o}] starting backup to {1}" -f (Get-Date), $Target | Tee-Object -FilePath $log -Append
& $Exe --backup $Target --quit --no-open 2>&1 | Tee-Object -FilePath $log -Append
$code = $LASTEXITCODE
"[{0:o}] finished with exit code {1} (0=ok, 1=backup failed, 2=no device)" -f (Get-Date), $code | Tee-Object -FilePath $log -Append
exit $code
