<#
.SYNOPSIS
    Builds the Reverie Windows players (one per sample theme) and zips them.

.DESCRIPTION
    1. Copies client/unity to a private work folder (Unity locks an open project, and the copy keeps
       its Library/ cache between runs, so later builds are much faster).
    2. Puts the sample themes into the copy's Assets/StreamingAssets/SampleThemes/.
    3. Runs Unity in batch mode: -buildTarget Win64 -executeMethod ReverieBuild.BuildWindowsThemes.
    4. Zips each player to <WorkDir>/dist/Reverie-<Theme>-Windows.zip (folder Reverie-<Theme>-Windows inside).

    See docs/BUILD.md for requirements, expected output and troubleshooting.

.PARAMETER ThemesDir
    A folder that contains SampleThemes/ (forest, beach), for example an unzipped
    Reverie-SampleThemes.zip or an existing project's Assets/StreamingAssets.

.PARAMETER ThemesZip
    A local path or https URL of Reverie-SampleThemes.zip. Used if ThemesDir is not given.

.PARAMETER WorkDir
    Where the project copy, the log and dist/ go. Default: <repo>/../_build/windows (outside the repo).

.PARAMETER UnityPath
    Unity.exe. Default: $env:UNITY_PATH, then the Unity Hub install of the version in
    ProjectSettings/ProjectVersion.txt.

.EXAMPLE
    pwsh scripts/build_windows.ps1 -ThemesZip D:\downloads\Reverie-SampleThemes.zip
#>
[CmdletBinding()]
param(
    [string]$ThemesDir,
    [string]$ThemesZip,
    [string]$WorkDir,
    [string]$UnityPath,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Source   = Join-Path $RepoRoot 'client\unity'
if (-not $WorkDir) { $WorkDir = Join-Path (Split-Path -Parent $RepoRoot) '_build\windows' }
$WorkDir  = [IO.Path]::GetFullPath($WorkDir)
$Project  = Join-Path $WorkDir 'unity'
$Dist     = Join-Path $WorkDir 'dist'
$Log      = Join-Path $WorkDir 'build.log'
$Themes   = Join-Path $Project 'Assets\StreamingAssets\SampleThemes'
$Expected = 'forest', 'beach'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- Unity editor -------------------------------------------------------------------------------
$Version = (Select-String -Path (Join-Path $Source 'ProjectSettings\ProjectVersion.txt') `
            -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
if (-not $UnityPath) { $UnityPath = $env:UNITY_PATH }
if (-not $UnityPath) {
    $roots = @("$env:ProgramFiles\Unity\Hub\Editor")
    $secondary = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
    if (Test-Path $secondary) {
        $p = (Get-Content $secondary -Raw | ConvertFrom-Json)
        if ($p) { $roots += $p }
    }
    foreach ($r in $roots) {
        $c = Join-Path $r "$Version\Editor\Unity.exe"
        if (Test-Path $c) { $UnityPath = $c; break }
    }
}
if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
    throw "Unity $Version not found. Install it with Unity Hub (Windows Build Support (Mono) is included " +
          "with the Windows editor), or pass -UnityPath / set UNITY_PATH."
}
$Modules = Join-Path (Split-Path -Parent $UnityPath) 'Data\PlaybackEngines\windowsstandalonesupport'
if (-not (Test-Path $Modules)) { throw "Unity at $UnityPath has no Windows Build Support module." }
Step "Unity $Version at $UnityPath"

# --- Project copy -------------------------------------------------------------------------------
Step "Copying $Source -> $Project"
New-Item -ItemType Directory -Force $WorkDir, $Dist | Out-Null
# /MIR keeps the copy in sync; excluded folders (Library cache, themes, outputs) are left alone.
robocopy $Source $Project /MIR /XD Library Temp Logs obj Builds UserSettings SampleThemes SourceData `
         /XF SampleThemes.meta api_config.json api_config.json.meta /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

# --- Sample themes ------------------------------------------------------------------------------
if (-not $ThemesDir -and $ThemesZip) {
    $zip = $ThemesZip
    if ($ThemesZip -match '^https?://') {
        $zip = Join-Path $WorkDir 'Reverie-SampleThemes.zip'
        if (-not (Test-Path $zip)) {
            Step "Downloading $ThemesZip"
            Invoke-WebRequest -Uri $ThemesZip -OutFile $zip -UseBasicParsing
        }
    }
    $ThemesDir = Join-Path $WorkDir 'themes'
    Step "Unzipping $zip"
    if (Test-Path $ThemesDir) { Remove-Item -Recurse -Force $ThemesDir }
    New-Item -ItemType Directory $ThemesDir | Out-Null
    tar.exe -xf $zip -C $ThemesDir
    if ($LASTEXITCODE) { throw "Could not unzip $zip" }
}
if ($ThemesDir) {
    $from = if (Test-Path (Join-Path $ThemesDir 'SampleThemes')) { $ThemesDir } else { Split-Path -Parent $ThemesDir }
    Step "Copying sample themes from $from"
    robocopy (Join-Path $from 'SampleThemes') $Themes /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    $meta = Join-Path $from 'SampleThemes.meta'
    if (Test-Path $meta) { Copy-Item $meta (Split-Path -Parent $Themes) -Force }
}
$missing = $Expected | Where-Object { -not (Test-Path (Join-Path $Themes "$_\scene.asset")) }
if ($missing) {
    throw "Sample themes missing in $Themes : $($missing -join ', '). Pass -ThemesDir or -ThemesZip " +
          "(see docs/BUILD.md)."
}

# --- Build --------------------------------------------------------------------------------------
# Unity's package manager clones UnityGaussianSplatting with git. On drives that don't record file
# ownership (exFAT, some network shares) git refuses with "not in a git directory"; trusting the
# work folder for this process only avoids changing the global git config.
$env:GIT_CONFIG_COUNT   = '1'
$env:GIT_CONFIG_KEY_0   = 'safe.directory'
$env:GIT_CONFIG_VALUE_0 = ($WorkDir -replace '\\', '/') + '/*'
$env:REVERIE_DOCS_DIR   = Join-Path $RepoRoot 'docs'

$Builds = Join-Path $Project 'Builds'
foreach ($t in $Expected) {
    $old = Join-Path $Builds ("Reverie-" + $t.Substring(0,1).ToUpper() + $t.Substring(1) + "-Windows")
    if (Test-Path $old) { Remove-Item -Recurse -Force $old }
}
Step "Building (log: $Log). The first run imports packages and takes 15-40 min."
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $UnityPath -PassThru -Wait -NoNewWindow -ArgumentList @(
    '-batchmode', '-nographics', '-quit', '-projectPath', "`"$Project`"", '-buildTarget', 'Win64',
    '-executeMethod', 'ReverieBuild.BuildWindowsThemes', '-logFile', "`"$Log`"")
Select-String -Path $Log -Pattern '^\[Reverie\]' | ForEach-Object { Write-Host $_.Line }
if ($p.ExitCode -ne 0) {
    Select-String -Path $Log -Pattern 'error CS|Error building|BuildFailedException|Aborting batchmode' |
        Select-Object -First 20 | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    throw "Unity exited with $($p.ExitCode). See $Log"
}
Step ("Built in {0:N0} min" -f $sw.Elapsed.TotalMinutes)

# --- Package ------------------------------------------------------------------------------------
if (-not $NoZip) {
    foreach ($dir in Get-ChildItem $Builds -Directory -Filter 'Reverie-*-Windows') {
        $zip = Join-Path $Dist "$($dir.Name).zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Step "Zipping $zip"
        # tar.exe ships with Windows 10+, is much faster than Compress-Archive and has no 2 GB limit.
        tar.exe -a -cf $zip -C $Builds $dir.Name
        if ($LASTEXITCODE) { throw "Could not zip $($dir.FullName)" }
    }
    Get-ChildItem $Dist -Filter '*.zip' | ForEach-Object {
        '{0}  {1:N0} MB  sha256 {2}' -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
}
Step "Players: $Builds\Reverie-<Theme>-Windows\Reverie.exe"
