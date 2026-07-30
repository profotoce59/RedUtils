<#
.SYNOPSIS
  Archive la version courante du bot dans un worktree Git dédié, buildé et renommé,
  prêt à être ajouté comme bot séparé dans RLBotGUI (pour comparer / faire s'affronter
  les versions).

.DESCRIPTION
  Enchaîne :
    1. git tag <version>
    2. git worktree add ../versions/RedUtils-<version> (branche archive/<version>)
    3. renomme le bot dans le Bot.cfg du worktree (name = MyBot-<version>)
    4. dotnet build Bot.sln dans ce worktree (sortie indépendante, pas de conflit de
       fichier avec le bot en cours d'exécution)

  Le tag ne capture QUE le dernier commit : committe tes changements avant, ou passe
  -AllowDirty pour archiver quand même l'état committé.

.EXAMPLE
  ./archive-version.ps1 protocev1.1.0
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

# --- Racine du repo = dossier du script ---
$repo = $PSScriptRoot
Set-Location $repo
if (-not (Test-Path (Join-Path $repo 'Bot.sln'))) {
    throw "Bot.sln introuvable dans '$repo'. Place archive-version.ps1 a la racine de RedUtils."
}

# --- Verifications AVANT de creer quoi que ce soit ---
$dirty = git status --porcelain
if ($dirty -and -not $AllowDirty) {
    Write-Host "Modifications non committees :" -ForegroundColor Yellow
    Write-Host $dirty
    throw "Le tag ne capture que le DERNIER COMMIT. Committe d'abord, ou relance avec -AllowDirty."
}
if (git tag --list $Version) {
    throw "Le tag '$Version' existe deja."
}

$versionsDir = Join-Path $repo '..\versions'
if (-not (Test-Path $versionsDir)) { New-Item -ItemType Directory -Path $versionsDir | Out-Null }
$versionsDir = (Resolve-Path $versionsDir).Path
$worktree = Join-Path $versionsDir "RedUtils-$Version"
if (Test-Path $worktree) {
    throw "Le dossier '$worktree' existe deja."
}

# --- 1. Tag ---
Write-Host "==> git tag $Version" -ForegroundColor Cyan
git tag $Version
if ($LASTEXITCODE -ne 0) { throw "git tag a echoue." }

# --- 2. Worktree ---
Write-Host "==> git worktree add (branche archive/$Version) -> $worktree" -ForegroundColor Cyan
git worktree add -b "archive/$Version" $worktree $Version
if ($LASTEXITCODE -ne 0) { throw "git worktree add a echoue." }

# --- 3. Renommer le bot dans le Bot.cfg du worktree ---
$cfg = Join-Path $worktree 'Bot.cfg'
Write-Host "==> Renommage : name = MyBot-$Version  ($cfg)" -ForegroundColor Cyan
$content = Get-Content $cfg
$renamed = $content -replace '^name\s*=\s*MyBot\s*$', "name = MyBot-$Version"
if (($content -join "`n") -eq ($renamed -join "`n")) {
    Write-Host "  ATTENTION : ligne 'name = MyBot' non trouvee, Bot.cfg inchange." -ForegroundColor Yellow
}
$renamed | Set-Content $cfg -Encoding ascii

# --- 4. Build (dans le worktree) ---
Write-Host "==> dotnet build Bot.sln (dans le worktree)" -ForegroundColor Cyan
Push-Location $worktree
try {
    dotnet build Bot.sln -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Le build du worktree a echoue." }
}
finally {
    Pop-Location
}

# --- Resume ---
Write-Host ""
Write-Host "Version '$Version' archivee." -ForegroundColor Green
Write-Host "  Worktree : $worktree"
Write-Host "  Bot.cfg  : $cfg  (name = MyBot-$Version)"
Write-Host ""
Write-Host "Dans RLBotGUI : 'Add' -> charge le Bot.cfg ci-dessus comme bot separe."
Write-Host "Tu peux alors faire s'affronter MyBot (courant) et MyBot-$Version."
