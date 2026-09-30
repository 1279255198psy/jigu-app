# FILE: jigu-app/tools/release.ps1
# Jigu release script -- turns a commit that is already on main into a GitHub Release.
#
# CI does the building and the publishing (.github/workflows/release.yml). This script only
# decides WHICH commit becomes a release, and it refuses to guess. It replaces the old
# double-click pair PUSH.cmd / RELEASE.cmd, which lived half inside and half outside the
# repository and disagreed with each other about almost everything.
#
# Typical use: commit + sync in the VS Code Source Control panel, then
#              Ctrl+Shift+P -> "Tasks: Run Task" -> "Jigu: release".
#
# Usage (from the repository root, in any shell):
#   powershell -NoProfile -File tools\release.ps1               # release the version in build.ps1
#   powershell -NoProfile -File tools\release.ps1 -Bump patch   # +1 the version, commit it, release
#   powershell -NoProfile -File tools\release.ps1 -Bump minor
#   powershell -NoProfile -File tools\release.ps1 -Version 0.2.0
#   powershell -NoProfile -File tools\release.ps1 -DryRun       # run every check, change nothing
#   powershell -NoProfile -File tools\release.ps1 -Build        # build.ps1 + --selfcheck first
#   powershell -NoProfile -File tools\release.ps1 -AllowDirty   # publish the committed revision
#
# Three things this script deliberately will NOT do:
#   * It never elevates. PUSH.cmd used to relaunch itself as administrator to comment out the
#     hosts entries pointing github.com at 127.0.0.1. A release script quietly editing a
#     security-relevant system file is a bad trade: when GitHub is out of reach this script
#     stops and tells you to run fix-hosts.ps1 yourself, in a window you control.
#   * It never uses -ExecutionPolicy Bypass. This machine is already RemoteSigned for the
#     current user, so a script inside the repository runs as-is. Should a machine ever block
#     it, unblock the file (Unblock-File) instead of disarming the policy.
#   * It never releases a dirty working tree unless you insist. CI builds the COMMIT, so
#     uncommitted edits are silently absent from the published package -- a failure that
#     stays invisible until a user reports it.
#
# ASCII only, on purpose: PowerShell 5.1 reads .ps1 with the ANSI code page unless the file
# carries a BOM, so a single stray CJK character here would be a parse error on this machine.
# Names that must be Chinese are built from code points, the same trick build.ps1 uses.
# build.ps1, tools\make-icon.ps1 and tests\make-merge-fixture.ps1 follow the same rule.

[CmdletBinding()]
param(
    [ValidateSet("patch", "minor", "major")]
    [string] $Bump,
    [string] $Version,
    [switch] $DryRun,
    [switch] $Build,
    [switch] $AllowDirty
)

$ErrorActionPreference = "Stop"

function Step($t) { Write-Host ""; Write-Host ("=== " + $t + " ===") -ForegroundColor Cyan }
function Ok($t)   { Write-Host ("  ok    " + $t) -ForegroundColor Green }
function Warn($t) { Write-Host ("  warn  " + $t) -ForegroundColor Yellow }
function Dry($t)  { Write-Host ("  dry   " + $t) -ForegroundColor DarkGray }
function Die($t) {
    Write-Host ""
    Write-Host ("  STOP  " + $t) -ForegroundColor Red
    Write-Host ""
    exit 1
}

# --- find the repository -----------------------------------------------------------
if ([string]::IsNullOrEmpty($PSScriptRoot)) {
    throw "PSScriptRoot is empty -- run this with -File, not piped through stdin"
}
$root = $PSScriptRoot
while (-not (Test-Path (Join-Path $root ".git"))) {
    $parent = Split-Path -Parent $root
    if ([string]::IsNullOrEmpty($parent) -or $parent -eq $root) {
        Die ("no .git found in " + $PSScriptRoot + " or in any parent directory")
    }
    $root = $parent
}
# Everything below runs relative to the repository root. This script is meant to be run as
# its own process (-File), so moving the location cannot leak into the caller's session.
Set-Location -LiteralPath $root

# --- find git ---------------------------------------------------------------------
$git = $null
$cmd = Get-Command git -ErrorAction SilentlyContinue
if ($cmd) { $git = $cmd.Source }
if (-not $git) {
    $candidates = @()
    if ($env:USERPROFILE)      { $candidates += (Join-Path $env:USERPROFILE ".workbuddy\vendor\PortableGit\cmd\git.exe") }
    if ($env:ProgramFiles)     { $candidates += (Join-Path $env:ProgramFiles "Git\cmd\git.exe") }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "Git\cmd\git.exe") }
    foreach ($p in $candidates) { if (Test-Path $p) { $git = $p; break } }
}
if (-not $git) { Die "git not found -- install Git for Windows or put it on PATH" }

# --- 1. repository state -----------------------------------------------------------
Step "repository"

$out  = @(& $git rev-parse --abbrev-ref HEAD)
$code = $LASTEXITCODE
if ($code -ne 0) { Die ("git rev-parse failed -- is " + $root + " really a git repository?") }
$branch = ($out -join "").Trim()
if ($branch -ne "main") {
    Die ("on branch '" + $branch + "'. Releases are cut from 'main'; switch with:  git switch main")
}
Ok ("branch " + $branch + "   (" + $root + ")")

$out  = @(& $git status --porcelain --untracked-files=no)
$code = $LASTEXITCODE
if ($code -ne 0) { Die "git status failed" }
if ($out.Count -gt 0) {
    Write-Host ""
    foreach ($line in $out) { Write-Host ("        " + $line) }
    Write-Host ""
    if (-not $AllowDirty) {
        Die "the working tree has uncommitted changes to tracked files. CI builds the commit, so these edits would be missing from the release. Commit them in the VS Code Source Control panel and run this again -- or pass -AllowDirty if you really do mean to publish the committed revision as it stands."
    }
    Warn "publishing the committed revision only; the changes listed above will NOT ship"
} else {
    Ok "working tree clean (tracked files)"
}

$out = @(& $git ls-files --others --exclude-standard)
if ($LASTEXITCODE -eq 0 -and $out.Count -gt 0) {
    Warn ("untracked files will not be in the release (" + $out.Count + "):")
    foreach ($line in $out) { Write-Host ("        " + $line) }
}

# --- 2. origin reachable, and not behind ------------------------------------------
Step "origin"

& $git fetch origin --tags --prune
if ($LASTEXITCODE -ne 0) {
    Die "git fetch failed. If github.com resolves to 127.0.0.1 on this machine, run fix-hosts.ps1 (it needs admin) and try again; if it is credentials, Git for Windows will pop a sign-in window. See RELEASE.md."
}

$out   = @(& $git rev-list --count "HEAD..origin/main")
$code  = $LASTEXITCODE
if ($code -ne 0) { Die ("could not compare with origin/main -- is this repository connected to a remote?") }
$behind = [int]($out -join "")
if ($behind -gt 0) {
    Die ("local main is " + $behind + " commit(s) behind origin/main. Integrate them first:  git pull --rebase origin main")
}
Ok "origin reachable, main is not behind"

# --- 3. version --------------------------------------------------------------------
Step "version"

$buildPath = Join-Path $root "build.ps1"
if (-not (Test-Path $buildPath)) { Die ("build.ps1 not found in " + $root) }

# Keep the pattern in step with the two other readers of this line: the CI gate
# (.github/workflows/release.yml) and the C# side, which compiles it in via Version.g.cs.
$buildText = [System.IO.File]::ReadAllText($buildPath)
$m = [regex]::Match($buildText, '(?m)^\$version = "([^"]+)"')
if (-not $m.Success) {
    Die 'build.ps1 has no $version = "x.y.z" line -- it is the single source of truth for the version'
}
$current = $m.Groups[1].Value
if ($current -notmatch '^\d+\.\d+\.\d+$') {
    Die ("build.ps1 declares version '" + $current + "', which is not of the form x.y.z")
}

$target = $current
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { Die ("-Version '" + $Version + "' is not of the form x.y.z") }
    $target = $Version
} elseif ($Bump) {
    $p = $current.Split(".")
    $a = [int]$p[0]
    $b = [int]$p[1]
    $c = [int]$p[2]
    if ($Bump -eq "major")     { $a = $a + 1; $b = 0; $c = 0 }
    elseif ($Bump -eq "minor") { $b = $b + 1; $c = 0 }
    else                       { $c = $c + 1 }
    $target = "$a.$b.$c"
}
$tag = "v" + $target
Ok ("build.ps1 says " + $current + "  ->  release tag " + $tag)

# Refuse to re-release a version. The CI gate compares the tag with build.ps1, so a re-run
# with the same number would not fail there -- it would just overwrite the old release and
# leave every installed copy convinced it is already up to date.
$out  = @(& $git ls-remote --tags origin ("refs/tags/" + $tag))
$code = $LASTEXITCODE
if ($code -ne 0) { Die "git ls-remote failed" }
if ($out.Count -gt 0) {
    Die ("tag " + $tag + " is already on origin -- that version has been released. Raise the version (run with -Bump patch, or edit the version line in build.ps1) and commit it.")
}
Ok ("tag " + $tag + " is still free on origin")

# The very first release of this project was tagged "0.1.0", without the "v" prefix, so the
# check above does not see it. Publishing v0.1.0 now would leave two Releases carrying one
# version number, and the updater reads whichever is newest. Warn instead of stopping:
# the old tag might be unrelated.
$out = @(& $git ls-remote --tags origin ("refs/tags/" + $target))
if ($LASTEXITCODE -eq 0 -and $out.Count -gt 0) {
    Warn ("origin also has a bare tag '" + $target + "' (no 'v' prefix). If that is this same version released the old way, move the number on instead (run with -Bump patch).")
}

# A local tag can exist without a remote one if an earlier run stopped between the two
# steps. Replacing it is safe and makes a retry work, so do that instead of failing.
$out = @(& $git tag --list $tag)
if ($LASTEXITCODE -eq 0 -and $out.Count -gt 0) {
    if ($DryRun) {
        Dry ("would delete a stale local tag " + $tag)
    } else {
        & $git tag -d $tag | Out-Null
        if ($LASTEXITCODE -ne 0) { Die ("could not delete the stale local tag " + $tag) }
        Warn ("deleted a stale local tag " + $tag + " left over from an earlier run")
    }
}

if ($target -ne $current) {
    # Splice the version line in place: everything else in build.ps1 stays byte-identical.
    $newText = $buildText.Substring(0, $m.Index) + '$version = "' + $target + '"' +
               $buildText.Substring($m.Index + $m.Length)
    if ($DryRun) {
        Dry ("would set build.ps1 to " + $target + " and commit it")
    } else {
        [System.IO.File]::WriteAllText($buildPath, $newText, (New-Object System.Text.UTF8Encoding($false)))
        & $git add -- build.ps1
        if ($LASTEXITCODE -ne 0) { Die "git add build.ps1 failed" }
        & $git commit -m ("release " + $tag)
        if ($LASTEXITCODE -ne 0) { Die "git commit failed -- nothing was tagged" }
        Ok ("build.ps1 " + $current + " -> " + $target + " (committed)")
    }
}

# --- 3b. the update dialog text comes from CHANGELOG.md ---------------------------
# release.yml takes the section whose heading is "## <version>" and writes it into
# app_version.json's "notes" -- the text the user reads in the "a new version is ready"
# dialog. A missing heading is NOT an error: notes quietly falls back to the last few
# commit subjects, which is the wrong text but ships without complaining. Check it here,
# where it costs nothing to fix, instead of finding out from a released dialog.
# The heading pattern is the one release.yml uses: ^##(?!#)\s*\[?v?<version>\b
$changelog = Join-Path $root "CHANGELOG.md"
if (-not (Test-Path -LiteralPath $changelog)) {
    Warn "no CHANGELOG.md -- the update dialog will show commit subjects, not release notes"
} else {
    $pattern = '^##(?!#)\s*\[?v?' + [System.Text.RegularExpressions.Regex]::Escape($target) + '\b'
    $found = $false
    foreach ($line in [System.IO.File]::ReadAllLines($changelog, (New-Object System.Text.UTF8Encoding($false)))) {
        if ([System.Text.RegularExpressions.Regex]::IsMatch($line.Trim(), $pattern)) { $found = $true; break }
    }
    if ($found) {
        Ok ("CHANGELOG.md has a '" + $target + "' section -- it becomes the update dialog text")
    } else {
        # The heading name is built from code points on purpose: this file must stay pure
        # ASCII, because PowerShell 5.1 reads a BOM-less .ps1 as GBK and would mangle any
        # literal CJK here. The three chars are U+672A U+53D1 U+5E03.
        $unreleased = [string][char]0x672A + [string][char]0x53D1 + [string][char]0x5E03
        Warn ("CHANGELOG.md has no '## " + $target + "' section -- the update dialog will fall back to commit subjects; rename the '## " + $unreleased + "' heading to '## " + $target + "' to ship your own text")
    }
}

# --- 4. optional local pre-flight --------------------------------------------------
if ($Build) {
    Step "local pre-flight (-Build)"
    & powershell -NoProfile -File $buildPath
    if ($LASTEXITCODE -ne 0) { Die "build.ps1 failed -- nothing was tagged" }

    $appName = [string][char]0x7A3D + [string][char]0x53E4          # the app's directory / exe name
    $appDir  = Join-Path (Join-Path $root "dist") $appName
    $exe     = Join-Path $appDir ($appName + ".exe")
    if (-not (Test-Path $exe)) { Die ("build.ps1 reported success but " + $exe + " is missing") }

    # The app is a GUI subsystem binary, so PowerShell will not wait for it and cannot read
    # an exit code from a plain call. Start-Process -Wait -PassThru is the only reliable way,
    # and it is what the CI step does too.
    $objDir = Join-Path $root "obj"
    if (-not (Test-Path $objDir)) { New-Item -ItemType Directory -Path $objDir | Out-Null }
    $report = Join-Path $objDir "selfcheck.txt"
    $proc = Start-Process -FilePath $exe -ArgumentList "--selfcheck", ('"' + $report + '"') `
                          -WorkingDirectory $appDir -PassThru -Wait
    Get-Content $report -ErrorAction SilentlyContinue | Write-Host
    if ($proc.ExitCode -ne 0) { Die ("--selfcheck failed with exit code " + $proc.ExitCode + " -- see " + $report) }
    if (-not (Select-String -Path $report -Pattern "SELFCHECK OK" -Quiet)) {
        Die ("--selfcheck did not report SELFCHECK OK -- see " + $report)
    }
    Ok "build.ps1 + --selfcheck passed"
}

# --- 5. push main ------------------------------------------------------------------
Step "push main"
if ($DryRun) {
    Dry "would run:  git push origin main"
} else {
    & $git push origin main
    if ($LASTEXITCODE -ne 0) {
        Die "git push origin main failed -- nothing was tagged. See the git message above (network, hosts block, or credentials)."
    }
    Ok "main pushed"
}

# --- 6. tag, and push the tag (this is what triggers CI) ---------------------------
Step "tag"
if ($DryRun) {
    Dry ("would run:  git tag -a " + $tag + " -m 'release " + $tag + "'")
    Dry ("would run:  git push origin " + $tag)
} else {
    & $git tag -a $tag -m ("release " + $tag)
    if ($LASTEXITCODE -ne 0) { Die ("git tag " + $tag + " failed") }
    & $git push origin $tag
    if ($LASTEXITCODE -ne 0) {
        Die ("git push origin " + $tag + " failed. The tag exists locally, so just fix the reason and re-run this script.")
    }
    Ok ("tag " + $tag + " pushed")
}

# --- what to watch -----------------------------------------------------------------
$url = (@(& $git remote get-url origin) -join "").Trim()
$repoUrl = $null
if ($url -match 'github\.com[:/]+([^/]+)/([^/]+?)(\.git)?$') {
    $repoUrl = "https://github.com/" + $Matches[1] + "/" + $Matches[2]
}

Write-Host ""
if ($DryRun) {
    Write-Host "  DRY RUN -- nothing was changed and nothing was pushed." -ForegroundColor Yellow
} else {
    Write-Host ("  " + $tag + " pushed. CI is building it now (about 3-6 minutes).") -ForegroundColor Green
}
if ($repoUrl) {
    Write-Host ("  actions : " + $repoUrl + "/actions")
    Write-Host ("  release : " + $repoUrl + "/releases/tag/" + $tag)
}
Write-Host ""
exit 0
