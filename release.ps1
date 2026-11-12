<#
.SYNOPSIS
    Cuts a new Prompty release: picks the next semantic version, updates the version and changelog,
    commits and tags. It never pushes.

.DESCRIPTION
    The bump comes from the Conventional Commit messages since the last v* tag:
      feat: ...                      -> minor  (1.2.3 -> 1.3.0)
      fix: / perf: / refactor: / ... -> patch  (1.2.3 -> 1.2.4)
    Major versions are never automatic. Pass -Major to go to the next major (1.2.3 -> 2.0.0).
    Commits marked as breaking (feat!: or "BREAKING CHANGE:") without -Major are released as a
    minor bump, with a warning.

    Steps: checks for a clean tree on main; computes the version; writes it to Prompty.csproj; moves
    CHANGELOG.md's "Unreleased" notes, plus generated entries for each commit, into a new version
    section; then commits "chore(release): vX.Y.Z" and makes an annotated tag vX.Y.Z.

    To publish, push the branch and tag. That is a separate step, done on purpose:
        git push origin main --follow-tags
    CI then builds Prompty.exe and creates the GitHub Release from the changelog section.

.EXAMPLE
    .\release.ps1 -DryRun        # show what would happen
    .\release.ps1                # automatic minor/patch release
    .\release.ps1 -Bump patch    # force a patch release
    .\release.ps1 -Major         # new major version (only when the owner decides)
#>
[CmdletBinding()]
param(
    [switch]$Major,
    [ValidateSet('auto', 'minor', 'patch')]
    [string]$Bump = 'auto',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csproj = Join-Path $root 'src\Prompty\Prompty.csproj'
$changelog = Join-Path $root 'CHANGELOG.md'
$repoUrl = 'https://github.com/WaifuShork/Prompty'

# Runs git; fails on a non-zero exit code. stderr is ignored, because Windows PowerShell treats git's
# harmless warnings there (e.g. about line endings) as errors.
function Invoke-Git {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & git.exe -C $root @args 2>$null }
    finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { throw "git $args failed (exit $LASTEXITCODE)" }
    $out
}

# -- preconditions --
if ((Invoke-Git status --porcelain)) { throw 'Working tree has uncommitted changes. Commit or stash them first.' }
$branch = Invoke-Git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { Write-Warning "Releasing from '$branch', not main." }

$lastTag = & git -C $root describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null
if ($LASTEXITCODE -ne 0 -or -not $lastTag) { throw 'No v* tag found. Tag the first release by hand (git tag -a v1.0.0).' }
$current = [version]($lastTag.TrimStart('v'))

# -- commits since the last release --
$sep = '<<<END>>>'
$raw = Invoke-Git log "$lastTag..HEAD" --no-merges --format="%h%x1f%s%x1f%b$sep"
$commits = @()
foreach ($chunk in (($raw -join "`n") -split [regex]::Escape($sep))) {
    $chunk = $chunk.Trim()
    if (-not $chunk) { continue }
    $parts = $chunk -split [char]0x1f
    $commits += [pscustomobject]@{ Hash = $parts[0]; Subject = $parts[1]; Body = ($parts[2..($parts.Count - 1)] -join "`n") }
}
$commits = $commits | Where-Object { $_.Subject -notmatch '^chore\(release\)' }
if (-not $commits) { Write-Host "Nothing to release: no commits since $lastTag."; return }

$ccRegex = '^(?<type>[a-z]+)(\((?<scope>[^)]+)\))?(?<bang>!)?:\s*(?<desc>.+)$'
$parsed = foreach ($c in $commits) {
    if ($c.Subject -match $ccRegex) {
        [pscustomobject]@{ Type = $Matches.type; Scope = $Matches.scope; Desc = $Matches.desc; Hash = $c.Hash
            Breaking = [bool]$Matches.bang -or $c.Body -match '(?m)^BREAKING[ -]CHANGE:' }
    } else {
        [pscustomobject]@{ Type = 'other'; Scope = $null; Desc = $c.Subject; Hash = $c.Hash; Breaking = $c.Body -match '(?m)^BREAKING[ -]CHANGE:' }
    }
}

# -- next version --
$hasFeat = @($parsed | Where-Object Type -eq 'feat').Count -gt 0
$hasBreaking = @($parsed | Where-Object Breaking).Count -gt 0
if ($Major) { $next = [version]::new($current.Major + 1, 0, 0); $kind = 'major' }
else {
    $kind = if ($Bump -ne 'auto') { $Bump } elseif ($hasFeat -or $hasBreaking) { 'minor' } else { 'patch' }
    $next = if ($kind -eq 'minor') { [version]::new($current.Major, $current.Minor + 1, 0) }
            else { [version]::new($current.Major, $current.Minor, $current.Build + 1) }
    if ($hasBreaking) { Write-Warning 'Breaking changes found. Major versions are only bumped with -Major, so this is released as a minor version.' }
}
$version = "$($next.Major).$($next.Minor).$($next.Build)"
$tag = "v$version"
if (Invoke-Git tag --list $tag) { throw "Tag $tag already exists." }

# -- changelog section --
$groups = [ordered]@{ 'Added' = @('feat'); 'Fixed' = @('fix'); 'Performance' = @('perf');
                      'Changed' = @('refactor', 'style', 'other', 'revert'); 'Documentation' = @('docs') }
$generated = New-Object System.Text.StringBuilder
foreach ($g in $groups.Keys) {
    $items = @($parsed | Where-Object { $_.Type -in $groups[$g] })
    if (-not $items) { continue }
    [void]$generated.AppendLine("### $g").AppendLine()
    foreach ($i in $items) {
        $scope = if ($i.Scope) { "**$($i.Scope):** " } else { '' }
        $bang = if ($i.Breaking) { ' **(breaking)**' } else { '' }
        $desc = $i.Desc.Substring(0, 1).ToUpper() + $i.Desc.Substring(1)
        [void]$generated.AppendLine("- $scope$desc$bang ([$($i.Hash)]($repoUrl/commit/$($i.Hash)))")
    }
    [void]$generated.AppendLine()
}
# build, ci, chore and test commits are left out of the notes but still count toward a patch release.

$text = Get-Content $changelog -Raw
# Notes run from the line after the heading up to the next "## [" heading or link reference at the START of
# a line. Anchoring to line starts stops the match from swallowing the previous release's section.
$unreleasedRx = '(?ms)^## \[Unreleased\][ \t]*\r?\n(?<notes>.*?)(?=^## \[|^\[[^\]]+\]: |\z)'
if ($text -notmatch $unreleasedRx) { throw 'CHANGELOG.md has no "## [Unreleased]" section.' }
$manual = $Matches.notes.Trim()
$date = Get-Date -Format 'yyyy-MM-dd'
$body = (@($manual, $generated.ToString().Trim()) | Where-Object { $_ }) -join "`n`n"
if (-not $body) { $body = '- Maintenance release.' }
$section = "## [Unreleased]`n`n## [$version] - $date`n`n$body`n`n"
$newText = [regex]::Replace($text, $unreleasedRx, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $section })
# Link references at the bottom, newest first under [Unreleased]
$links = "[Unreleased]: $repoUrl/compare/$tag...HEAD`n[$version]: $repoUrl/compare/$lastTag...$tag"
$newText = [regex]::Replace($newText, '(?m)^\[Unreleased\]: .*$', [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $links })

Write-Host "`n$lastTag -> $tag ($kind, $(@($parsed).Count) commits)" -ForegroundColor Cyan
Write-Host "`n## [$version] - $date`n`n$body" -ForegroundColor Gray
if ($DryRun) { Write-Host "`nDry run: nothing changed." -ForegroundColor Yellow; return }

# -- write, commit, tag --
$proj = Get-Content $csproj -Raw
$proj = [regex]::Replace($proj, '<Version>[^<]*</Version>', "<Version>$version</Version>")
[IO.File]::WriteAllText($csproj, $proj)
[IO.File]::WriteAllText($changelog, ($newText -replace "`r?`n", "`r`n"))

Invoke-Git add -- $csproj $changelog | Out-Null
Invoke-Git commit -q -m "chore(release): $tag" | Out-Null
Invoke-Git tag -a $tag -m "Prompty $version" | Out-Null

Write-Host "`nCommitted and tagged $tag (not pushed)." -ForegroundColor Green
Write-Host "Publish with:  git push origin $branch --follow-tags"
