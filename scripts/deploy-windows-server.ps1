#requires -Version 5.1

<#
.SYNOPSIS
Tests, publishes, and deploys CRM Logic Lens to an existing IIS site on Windows Server.

.DESCRIPTION
This script is intended for repeat deployments after IIS, HTTPS, Windows Authentication,
the application pool, directory permissions, and PowerShell remoting have been configured.

It publishes the API and decompiler worker locally, transfers versioned archives through a
PowerShell session, preserves appsettings.Local.json and appsettings.Production.json,
backs up the existing binaries, restarts the IIS application pool, and checks /health.
If deployment or the health check fails, the previous server files are restored.

.EXAMPLE
./scripts/deploy-windows-server.ps1 `
  -ComputerName AI-SERVER01 `
  -SiteRoot 'D:\CrmLogicLens\Api' `
  -DecompilerRoot 'D:\CrmLogicLens\Decompiler' `
  -BackupRoot 'D:\CrmLogicLens\Backups' `
  -AppPoolName 'CrmLogicLensPool' `
  -HealthUrl 'https://logiclens.contoso.local/health'

.EXAMPLE
$credential = Get-Credential 'CONTOSO\deploy.logiclens'
./scripts/deploy-windows-server.ps1 `
  -ComputerName AI-SERVER01 `
  -Credential $credential `
  -SiteRoot 'D:\CrmLogicLens\Api' `
  -DecompilerRoot 'D:\CrmLogicLens\Decompiler' `
  -BackupRoot 'D:\CrmLogicLens\Backups' `
  -AppPoolName 'CrmLogicLensPool' `
  -HealthUrl 'https://logiclens.contoso.local/health' `
  -SkipTests
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ComputerName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SiteRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DecompilerRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AppPoolName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNull()]
    [uri]$HealthUrl,

    [PSCredential]$Credential,

    [ValidateRange(1, 30)]
    [int]$KeepBackups = 5,

    [ValidateRange(15, 300)]
    [int]$HealthTimeoutSeconds = 90,

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'CrmLogicLens.sln'
$apiProject = Join-Path $repositoryRoot 'src\CrmLogicLens.Api\CrmLogicLens.Api.csproj'
$workerProject = Join-Path $repositoryRoot 'src\CrmLogicLens.Decompiler.Worker\CrmLogicLens.Decompiler.Worker.csproj'
$deploymentId = [Guid]::NewGuid().ToString('N')
$localStaging = Join-Path ([IO.Path]::GetTempPath()) "CrmLogicLensDeploy-$deploymentId"
$apiPublish = Join-Path $localStaging 'api'
$workerPublish = Join-Path $localStaging 'decompiler'
$apiArchive = Join-Path $localStaging 'api.zip'
$workerArchive = Join-Path $localStaging 'decompiler.zip'
$remoteStaging = "`$env:TEMP\CrmLogicLensDeploy-$deploymentId"
$resolvedRemoteStaging = $null
$session = $null
$deployment = $null

function Write-Stage {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Test-DeploymentHealth {
    param(
        [Parameter(Mandatory = $true)][uri]$Uri,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $result = Invoke-RestMethod `
                -Uri $Uri `
                -Method Get `
                -UseDefaultCredentials `
                -TimeoutSec 15 `
                -Headers @{ 'Cache-Control' = 'no-cache' }
            if ([string]$result -eq 'Healthy') {
                return
            }
            $lastError = "Health endpoint returned '$result'."
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Seconds 3
    }
    throw "Health check did not become healthy within $TimeoutSeconds seconds. Last error: $lastError"
}

function Assert-LocalStagingPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $tempPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($tempPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($fullPath)).StartsWith('CrmLogicLensDeploy-', [StringComparison]::Ordinal)) {
        throw "Refusing to use unsafe local staging path '$fullPath'."
    }
}

if ($HealthUrl.Scheme -ne 'https' -and $HealthUrl.Scheme -ne 'http') {
    throw 'HealthUrl must use HTTP or HTTPS.'
}
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $apiProject -PathType Leaf) -or
    -not (Test-Path -LiteralPath $workerProject -PathType Leaf)) {
    throw "Run this script from a complete CRM Logic Lens repository checkout. Repository: $repositoryRoot"
}

$deploymentTarget = "$ComputerName ($SiteRoot, $DecompilerRoot)"
if (-not $PSCmdlet.ShouldProcess($deploymentTarget, 'Publish and deploy CRM Logic Lens with automatic rollback')) {
    return
}

Assert-LocalStagingPath -Path $localStaging

$remoteDeployScript = {
    param(
        [string]$RemoteStaging,
        [string]$SiteRoot,
        [string]$DecompilerRoot,
        [string]$BackupRoot,
        [string]$AppPoolName,
        [string]$DeploymentId
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    function Resolve-SafeDirectory {
        param([string]$Path, [string]$Label)
        if (-not [IO.Path]::IsPathRooted($Path)) {
            throw "$Label must be an absolute path."
        }
        $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        $rootPath = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\')
        if ([string]::IsNullOrWhiteSpace($fullPath) -or
            $fullPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.Length -lt 8) {
            throw "Refusing unsafe $Label path '$fullPath'."
        }
        return $fullPath
    }

    function Test-PathInside {
        param([string]$Child, [string]$Parent)
        $parentPrefix = $Parent.TrimEnd('\') + '\'
        return $Child.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)
    }

    function Clear-SafeDirectory {
        param([string]$Path)
        $prefix = $Path.TrimEnd('\') + '\'
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) {
            $resolvedItem = [IO.Path]::GetFullPath($item.FullName)
            if (-not $resolvedItem.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove item outside deployment directory: $resolvedItem"
            }
            Remove-Item -LiteralPath $resolvedItem -Recurse -Force
        }
    }

    function Copy-DirectoryContents {
        param([string]$Source, [string]$Destination)
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        foreach ($item in @(Get-ChildItem -LiteralPath $Source -Force -ErrorAction SilentlyContinue)) {
            Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
        }
    }

    function Stop-DeploymentAppPool {
        param([string]$Name)
        $state = (Get-WebAppPoolState -Name $Name).Value
        if ($state -ne 'Stopped') {
            Stop-WebAppPool -Name $Name
        }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ((Get-WebAppPoolState -Name $Name).Value -ne 'Stopped') {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw "IIS application pool '$Name' did not stop within 30 seconds."
            }
            Start-Sleep -Milliseconds 500
        }
    }

    Import-Module WebAdministration -ErrorAction Stop

    $safeSiteRoot = Resolve-SafeDirectory $SiteRoot 'SiteRoot'
    $safeDecompilerRoot = Resolve-SafeDirectory $DecompilerRoot 'DecompilerRoot'
    $safeBackupRoot = Resolve-SafeDirectory $BackupRoot 'BackupRoot'
    $safeRemoteStaging = Resolve-SafeDirectory $RemoteStaging 'remote staging'

    if ($safeSiteRoot.Equals($safeDecompilerRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-PathInside $safeSiteRoot $safeDecompilerRoot) -or
        (Test-PathInside $safeDecompilerRoot $safeSiteRoot)) {
        throw 'SiteRoot and DecompilerRoot must be separate, non-nested directories.'
    }
    if ((Test-PathInside $safeBackupRoot $safeSiteRoot) -or
        (Test-PathInside $safeBackupRoot $safeDecompilerRoot) -or
        (Test-PathInside $safeSiteRoot $safeBackupRoot) -or
        (Test-PathInside $safeDecompilerRoot $safeBackupRoot)) {
        throw 'BackupRoot must be outside SiteRoot and DecompilerRoot.'
    }
    if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
        throw "IIS application pool '$AppPoolName' does not exist. Complete the one-time IIS setup first."
    }

    $apiArchive = Join-Path $safeRemoteStaging 'api.zip'
    $workerArchive = Join-Path $safeRemoteStaging 'decompiler.zip'
    if (-not (Test-Path -LiteralPath $apiArchive -PathType Leaf) -or
        -not (Test-Path -LiteralPath $workerArchive -PathType Leaf)) {
        throw 'The deployment archives were not transferred to the server staging directory.'
    }

    $expandedApi = Join-Path $safeRemoteStaging 'api'
    $expandedWorker = Join-Path $safeRemoteStaging 'decompiler'
    New-Item -ItemType Directory -Path $expandedApi, $expandedWorker -Force | Out-Null
    Expand-Archive -LiteralPath $apiArchive -DestinationPath $expandedApi -Force
    Expand-Archive -LiteralPath $workerArchive -DestinationPath $expandedWorker -Force
    if (-not (Test-Path -LiteralPath (Join-Path $expandedApi 'CrmLogicLens.Api.dll') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $expandedWorker 'CrmLogicLens.Decompiler.Worker.exe') -PathType Leaf)) {
        throw 'Published archives are incomplete; deployment was not started.'
    }

    New-Item -ItemType Directory -Path $safeSiteRoot, $safeDecompilerRoot, $safeBackupRoot -Force | Out-Null
    $releaseName = "release-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))-$DeploymentId"
    $releaseBackup = Join-Path $safeBackupRoot $releaseName
    $apiBackup = Join-Path $releaseBackup 'Api'
    $workerBackup = Join-Path $releaseBackup 'Decompiler'
    $preserved = Join-Path $safeRemoteStaging 'preserved'
    New-Item -ItemType Directory -Path $apiBackup, $workerBackup, $preserved -Force | Out-Null

    $hadSiteContent = @(Get-ChildItem -LiteralPath $safeSiteRoot -Force -ErrorAction SilentlyContinue).Count -gt 0
    $hadWorkerContent = @(Get-ChildItem -LiteralPath $safeDecompilerRoot -Force -ErrorAction SilentlyContinue).Count -gt 0
    if ($hadSiteContent) { Copy-DirectoryContents $safeSiteRoot $apiBackup }
    if ($hadWorkerContent) { Copy-DirectoryContents $safeDecompilerRoot $workerBackup }

    foreach ($fileName in @('appsettings.Local.json', 'appsettings.Production.json')) {
        $sourceFile = Join-Path $safeSiteRoot $fileName
        if (Test-Path -LiteralPath $sourceFile -PathType Leaf) {
            Copy-Item -LiteralPath $sourceFile -Destination $preserved -Force
        }
    }

    $appPoolStopped = $false
    try {
        Stop-DeploymentAppPool $AppPoolName
        $appPoolStopped = $true
        Clear-SafeDirectory $safeSiteRoot
        Clear-SafeDirectory $safeDecompilerRoot
        Copy-DirectoryContents $expandedApi $safeSiteRoot
        Copy-DirectoryContents $expandedWorker $safeDecompilerRoot
        Copy-DirectoryContents $preserved $safeSiteRoot
        Start-WebAppPool -Name $AppPoolName
        $appPoolStopped = $false
    }
    catch {
        $deploymentError = $_
        try {
            if (-not $appPoolStopped) {
                Stop-DeploymentAppPool $AppPoolName
            }
            Clear-SafeDirectory $safeSiteRoot
            Clear-SafeDirectory $safeDecompilerRoot
            if ($hadSiteContent) { Copy-DirectoryContents $apiBackup $safeSiteRoot }
            if ($hadWorkerContent) { Copy-DirectoryContents $workerBackup $safeDecompilerRoot }
            Start-WebAppPool -Name $AppPoolName
        }
        catch {
            throw "Deployment failed and automatic rollback also failed. Deployment: $($deploymentError.Exception.Message) Rollback: $($_.Exception.Message)"
        }
        throw "Deployment failed; the previous files were restored. $($deploymentError.Exception.Message)"
    }

    [PSCustomObject]@{
        BackupPath       = $releaseBackup
        HadSiteContent   = $hadSiteContent
        HadWorkerContent = $hadWorkerContent
        SiteRoot         = $safeSiteRoot
        DecompilerRoot   = $safeDecompilerRoot
        AppPoolName      = $AppPoolName
        DeploymentId     = $DeploymentId
    }
}

$remoteRollbackScript = {
    param([object]$Deployment)

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    function Clear-SafeDirectory {
        param([string]$Path)
        $safePath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        $rootPath = [IO.Path]::GetPathRoot($safePath).TrimEnd('\')
        if ($safePath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or $safePath.Length -lt 8) {
            throw "Refusing unsafe rollback path '$safePath'."
        }
        $prefix = $safePath + '\'
        foreach ($item in @(Get-ChildItem -LiteralPath $safePath -Force -ErrorAction SilentlyContinue)) {
            $resolvedItem = [IO.Path]::GetFullPath($item.FullName)
            if (-not $resolvedItem.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove item outside rollback directory: $resolvedItem"
            }
            Remove-Item -LiteralPath $resolvedItem -Recurse -Force
        }
    }

    function Copy-DirectoryContents {
        param([string]$Source, [string]$Destination)
        foreach ($item in @(Get-ChildItem -LiteralPath $Source -Force -ErrorAction SilentlyContinue)) {
            Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
        }
    }

    Import-Module WebAdministration -ErrorAction Stop
    if ((Get-WebAppPoolState -Name $Deployment.AppPoolName).Value -ne 'Stopped') {
        Stop-WebAppPool -Name $Deployment.AppPoolName
        Start-Sleep -Seconds 2
    }
    Clear-SafeDirectory $Deployment.SiteRoot
    Clear-SafeDirectory $Deployment.DecompilerRoot
    if ($Deployment.HadSiteContent) {
        Copy-DirectoryContents (Join-Path $Deployment.BackupPath 'Api') $Deployment.SiteRoot
    }
    if ($Deployment.HadWorkerContent) {
        Copy-DirectoryContents (Join-Path $Deployment.BackupPath 'Decompiler') $Deployment.DecompilerRoot
    }
    Start-WebAppPool -Name $Deployment.AppPoolName
}

$remoteTrimBackupsScript = {
    param([string]$BackupRoot, [int]$KeepBackups)
    $safeBackupRoot = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
    $rootPath = [IO.Path]::GetPathRoot($safeBackupRoot).TrimEnd('\')
    if ($safeBackupRoot.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or $safeBackupRoot.Length -lt 8) {
        throw "Refusing unsafe backup path '$safeBackupRoot'."
    }
    $prefix = $safeBackupRoot + '\'
    $oldReleases = @(Get-ChildItem -LiteralPath $safeBackupRoot -Directory -Force |
        Where-Object { $_.Name -match '^release-\d{8}-\d{6}-[a-f0-9]{32}$' } |
        Sort-Object CreationTimeUtc -Descending |
        Select-Object -Skip $KeepBackups)
    foreach ($release in $oldReleases) {
        $resolvedRelease = [IO.Path]::GetFullPath($release.FullName)
        if (-not $resolvedRelease.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove backup outside backup root: $resolvedRelease"
        }
        Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
    }
}

try {
    Write-Stage 'Preparing local staging directory'
    New-Item -ItemType Directory -Path $apiPublish, $workerPublish -Force | Out-Null

    if (-not $SkipTests) {
        Write-Stage 'Running automated tests'
        Invoke-DotNet @('test', $solutionPath, '--configuration', 'Release', '--nologo')
    }

    Write-Stage 'Publishing ASP.NET Core API'
    Invoke-DotNet @(
        'publish', $apiProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'false',
        '--output', $apiPublish,
        '--nologo'
    )

    Write-Stage 'Publishing isolated decompiler worker'
    Invoke-DotNet @(
        'publish', $workerProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'false',
        '--output', $workerPublish,
        '--nologo'
    )

    if (Test-Path -LiteralPath $apiArchive) { Remove-Item -LiteralPath $apiArchive -Force }
    if (Test-Path -LiteralPath $workerArchive) { Remove-Item -LiteralPath $workerArchive -Force }
    Compress-Archive -Path (Join-Path $apiPublish '*') -DestinationPath $apiArchive -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $workerPublish '*') -DestinationPath $workerArchive -CompressionLevel Optimal

    Write-Stage "Opening PowerShell session to $ComputerName"
    $sessionArguments = @{ ComputerName = $ComputerName }
    if ($Credential) { $sessionArguments.Credential = $Credential }
    $session = New-PSSession @sessionArguments

    Invoke-Command -Session $session -ScriptBlock {
        param([string]$RemoteStaging)
        $fullPath = [IO.Path]::GetFullPath($ExecutionContext.InvokeCommand.ExpandString($RemoteStaging))
        $tempPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $fullPath.StartsWith($tempPath, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($fullPath)).StartsWith('CrmLogicLensDeploy-', [StringComparison]::Ordinal)) {
            throw "Refusing unsafe server staging path '$fullPath'."
        }
        New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    } -ArgumentList $remoteStaging

    $resolvedRemoteStaging = Invoke-Command -Session $session -ScriptBlock {
        param([string]$RemoteStaging)
        [IO.Path]::GetFullPath($ExecutionContext.InvokeCommand.ExpandString($RemoteStaging))
    } -ArgumentList $remoteStaging
    Copy-Item -LiteralPath $apiArchive -Destination (Join-Path $resolvedRemoteStaging 'api.zip') -ToSession $session
    Copy-Item -LiteralPath $workerArchive -Destination (Join-Path $resolvedRemoteStaging 'decompiler.zip') -ToSession $session

    Write-Stage 'Backing up and replacing server binaries'
    $deployment = Invoke-Command `
        -Session $session `
        -ScriptBlock $remoteDeployScript `
        -ArgumentList $resolvedRemoteStaging, $SiteRoot, $DecompilerRoot, $BackupRoot, $AppPoolName, $deploymentId

    Write-Stage "Checking $HealthUrl"
    try {
        Test-DeploymentHealth -Uri $HealthUrl -TimeoutSeconds $HealthTimeoutSeconds
    }
    catch {
        $healthError = $_
        Write-Warning 'The new release failed its health check. Restoring the previous server release.'
        Invoke-Command -Session $session -ScriptBlock $remoteRollbackScript -ArgumentList $deployment
        throw "Deployment health check failed and the previous release was restored. $($healthError.Exception.Message)"
    }

    Invoke-Command `
        -Session $session `
        -ScriptBlock $remoteTrimBackupsScript `
        -ArgumentList $BackupRoot, $KeepBackups

    Write-Host "`nDeployment completed successfully." -ForegroundColor Green
    Write-Host "Server:  $ComputerName"
    Write-Host "Health:  $HealthUrl"
    Write-Host "Backup:  $($deployment.BackupPath)"
}
finally {
    if ($session) {
        if ($resolvedRemoteStaging) {
            try {
                Invoke-Command -Session $session -ScriptBlock {
                    param([string]$RemoteStaging)
                    $fullPath = [IO.Path]::GetFullPath($RemoteStaging)
                    $tempPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
                    if (-not $fullPath.StartsWith($tempPath, [StringComparison]::OrdinalIgnoreCase) -or
                        -not ([IO.Path]::GetFileName($fullPath)).StartsWith('CrmLogicLensDeploy-', [StringComparison]::Ordinal)) {
                        throw "Refusing to remove unsafe server staging path '$fullPath'."
                    }
                    if (Test-Path -LiteralPath $fullPath) {
                        Remove-Item -LiteralPath $fullPath -Recurse -Force
                    }
                } -ArgumentList $resolvedRemoteStaging -ErrorAction SilentlyContinue
            }
            catch { }
        }
        try { Remove-PSSession -Session $session -ErrorAction SilentlyContinue } catch { }
    }
    if (Test-Path -LiteralPath $localStaging) {
        Assert-LocalStagingPath -Path $localStaging
        Remove-Item -LiteralPath $localStaging -Recurse -Force
    }
}
