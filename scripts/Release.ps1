#Requires -Version 5.1

[CmdletBinding(DefaultParameterSetName = 'Current')]
param(
    [Parameter(ParameterSetName = 'Explicit')]
    [string] $Version,

    [Parameter(ParameterSetName = 'Bump')]
    [ValidateSet('patch', 'minor', 'major')]
    [string] $Bump,

    [string[]] $Runtime,

    [switch] $Upload,
    [switch] $PreRelease,
    [switch] $Draft,
    [switch] $SkipPublish,
    [switch] $SkipDownload,
    [switch] $Tag,
    [switch] $PushTag,
    [string] $NotesPath,
    [string] $RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $RepoRoot = (Get-Location).Path
    }
    else {
        $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    }
}

function Get-ReleaseConfig {
    param([string] $Root)

    $configPath = Join-Path $Root 'release\config.json'
    if (-not (Test-Path $configPath)) {
        throw "Release config not found: $configPath"
    }

    return Get-Content $configPath -Raw | ConvertFrom-Json
}

function Get-ReleaseVersion {
    param([string] $Root)

    $versionPath = Join-Path $Root 'release\VERSION'
    if (-not (Test-Path $versionPath)) {
        throw "Release version file not found: $versionPath"
    }

    return (Get-Content $versionPath -Raw).Trim()
}

function Set-ReleaseVersion {
    param(
        [string] $Root,
        [string] $NewVersion
    )

    if ($NewVersion -notmatch '^\d+\.\d+\.\d+([\-+].+)?$') {
        throw "Version must be semver2 (e.g. 1.2.3 or 1.2.3-beta.1). Got: $NewVersion"
    }

    $versionPath = Join-Path $Root 'release\VERSION'
    Set-Content -Path $versionPath -Value $NewVersion -NoNewline -Encoding utf8
}

function Get-BumpedVersion {
    param(
        [string] $Current,
        [string] $Part
    )

    if ($Current -match '^(\d+)\.(\d+)\.(\d+)') {
        $major = [int] $Matches[1]
        $minor = [int] $Matches[2]
        $patch = [int] $Matches[3]
        $suffix = ''
        if ($Current -match '^(\d+\.\d+\.\d+)(.*)$') {
            $suffix = $Matches[2]
        }

        switch ($Part) {
            'major' { $major++; $minor = 0; $patch = 0 }
            'minor' { $minor++; $patch = 0 }
            'patch' { $patch++ }
        }

        return "$major.$minor.$patch$suffix"
    }

    throw "Cannot bump non-semver version: $Current"
}

function Get-GitHubToken {
    if ($env:GITHUB_TOKEN) {
        return $env:GITHUB_TOKEN
    }

    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $token = gh auth token 2>$null
        if ($LASTEXITCODE -eq 0 -and $token) {
            return $token.Trim()
        }
    }

    if ($Upload) {
        throw 'Upload requires GITHUB_TOKEN or GitHub CLI authentication (gh auth login).'
    }

    return $null
}

function Ensure-VpkTool {
    param([string] $Root)

    Push-Location $Root
    try {
        dotnet tool restore | Out-Host
    }
    finally {
        Pop-Location
    }
}

function Invoke-Vpk {
    param([string[]] $Arguments)

    & dotnet vpk @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "vpk failed: vpk $($Arguments -join ' ')"
    }
}

function Get-RuntimeConfig {
    param(
        $Config,
        [string] $Rid
    )

    $runtime = $Config.runtimes | Where-Object { $_.rid -eq $Rid } | Select-Object -First 1
    if (-not $runtime) {
        throw "Runtime '$Rid' is not configured in release/config.json."
    }

    return $runtime
}

function Publish-Runtime {
    param(
        [string] $Root,
        $Config,
        $RuntimeConfig,
        [string] $ReleaseVersion,
        [string] $Token,
        [string] $Notes,
        [bool] $DoUpload,
        [bool] $IsPreRelease,
        [bool] $IsDraft,
        [bool] $DoDownload
    )

    $rid = [string] $RuntimeConfig.rid
    $channel = [string] $RuntimeConfig.channel
    $mainExe = [string] $RuntimeConfig.mainExe
    $publishDir = Join-Path $Root "artifacts\publish\$rid"
    $outputDir = Join-Path $Root "artifacts\Releases\$rid"
    $projectPath = Join-Path $Root 'Stndr\Stndr.csproj'
    $iconPath = Join-Path $Root ([string] $Config.icon)

    Write-Host "==> [$rid] Preparing release $ReleaseVersion"

    New-Item -ItemType Directory -Force -Path $publishDir, $outputDir | Out-Null

    Get-ChildItem $outputDir -Include '*.nupkg', 'releases.*.json', 'assets.*.json', 'RELEASES' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    if ($DoDownload) {
        Write-Host "==> [$rid] Downloading previous release for delta packages"
        $downloadArgs = @(
            'download', 'github',
            '--repoUrl', [string] $Config.repoUrl,
            '--outputDir', $outputDir,
            '--channel', $channel
        )
        if ($Token) {
            $downloadArgs += @('--token', $Token)
        }
        if ($IsPreRelease) {
            $downloadArgs += '--pre'
        }

        & dotnet vpk @downloadArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "[$rid] No previous release downloaded (first release for this channel?)."
        }
    }

    if (-not $SkipPublish) {
        Write-Host "==> [$rid] Publishing application"
        dotnet publish $projectPath `
            -c Release `
            -r $rid `
            --self-contained `
            -o $publishDir `
            /p:Version=$ReleaseVersion | Out-Host

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $rid"
        }
    }
    else {
        Write-Host "==> [$rid] Skipping publish (using existing artifacts)"
        if (-not (Test-Path (Join-Path $publishDir $mainExe))) {
            throw "Publish output not found for ${rid}: $(Join-Path $publishDir $mainExe)"
        }
    }

    Write-Host "==> [$rid] Packing Velopack release"
    $packArgs = @(
        'pack',
        '-u', [string] $Config.packId,
        '-v', $ReleaseVersion,
        '-p', $publishDir,
        '-e', $mainExe,
        '--packTitle', [string] $Config.packTitle,
        '-i', $iconPath,
        '-r', $rid,
        '-c', $channel,
        '-o', $outputDir
    )

    if ($Notes) {
        $packArgs += @('--releaseNotes', $Notes)
    }

    Invoke-Vpk $packArgs

    if ($DoUpload) {
        Write-Host "==> [$rid] Uploading to GitHub Releases"
        $uploadArgs = @(
            'upload', 'github',
            '--repoUrl', [string] $Config.repoUrl,
            '--outputDir', $outputDir,
            '--channel', $channel,
            '--tag', "v$ReleaseVersion",
            '--releaseName', "$($Config.packTitle) $ReleaseVersion",
            '--merge'
        )

        if ($Token) {
            $uploadArgs += @('--token', $Token)
        }

        if ($IsPreRelease) {
            $uploadArgs += '--pre'
        }

        if (-not $IsDraft) {
            $uploadArgs += '--publish'
        }

        Invoke-Vpk $uploadArgs
    }

    Write-Host "==> [$rid] Done. Output: $outputDir"
}

$config = Get-ReleaseConfig -Root $RepoRoot
$releaseVersion = switch ($PSCmdlet.ParameterSetName) {
    'Explicit' { $Version.Trim() }
    'Bump' {
        $current = Get-ReleaseVersion -Root $RepoRoot
        Get-BumpedVersion -Current $current -Part $Bump
    }
    default { Get-ReleaseVersion -Root $RepoRoot }
}

if ($PSCmdlet.ParameterSetName -ne 'Current') {
    Set-ReleaseVersion -Root $RepoRoot -NewVersion $releaseVersion
    Write-Host "Set release/VERSION to $releaseVersion"
}

if (-not $NotesPath) {
    $NotesPath = Join-Path $RepoRoot 'release\NOTES.md'
}

if (-not (Test-Path $NotesPath)) {
    throw "Release notes file not found: $NotesPath"
}

if ($Runtime) {
    $selectedRuntimes = $Runtime
}
elseif ($env:OS -eq 'Windows_NT') {
    $selectedRuntimes = @('win-x64')
}
else {
    $selectedRuntimes = @($config.runtimes | ForEach-Object { $_.rid })
}

$token = Get-GitHubToken
Ensure-VpkTool -Root $RepoRoot

Write-Host "Stndr release $releaseVersion"
Write-Host "Runtimes: $($selectedRuntimes -join ', ')"
if ($Upload) {
    Write-Host 'Upload: enabled'
    if ($PreRelease) {
        Write-Host 'GitHub release: pre-release'
    }
    if ($Draft) {
        Write-Host 'GitHub release: draft'
    }
    elseif (-not $PreRelease) {
        Write-Host 'GitHub release: published'
    }
    else {
        Write-Host 'GitHub release: published pre-release'
    }
}

foreach ($rid in $selectedRuntimes) {
    $runtimeConfig = Get-RuntimeConfig -Config $config -Rid $rid
    Publish-Runtime `
        -Root $RepoRoot `
        -Config $config `
        -RuntimeConfig $runtimeConfig `
        -ReleaseVersion $releaseVersion `
        -Token $token `
        -Notes $NotesPath `
        -DoUpload ([bool]$Upload) `
        -IsPreRelease ([bool]$PreRelease) `
        -IsDraft ([bool]$Draft) `
        -DoDownload (-not $SkipDownload.IsPresent)
}

if ($Tag) {
    $tagName = "v$releaseVersion"
    Push-Location $RepoRoot
    try {
        git tag -a $tagName -m "Release $releaseVersion" -f
        Write-Host "Tagged $tagName"
        if ($PushTag) {
            git push origin $tagName
            Write-Host "Pushed $tagName to origin"
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host ''
Write-Host "Release $releaseVersion is ready."
if (-not $Upload) {
    Write-Host 'Local packages are under artifacts/Releases/.'
    Write-Host 'Upload with:  .\scripts\Release.ps1 -Upload'
}
else {
    Write-Host "GitHub release: $(( [string] $config.repoUrl ).TrimEnd('/'))/releases/tag/v$releaseVersion"
}