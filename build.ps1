<#
  Builds a BetterPlay release and publishes it to the plugin repository.

    .\build.ps1 -Version 0.1.0.0 -Changelog "Probe: records how apps request and play video"            # build + zip only
    .\build.ps1 -Version 0.1.0.0 -Changelog "..." -Publish                                                # also release on GitHub

  Steps: compile against the lowest 12.x Jellyfin package (one build for every 12.x server), zip the plugin DLL,
  MD5 the zip, add the version to manifest.json (newest first). With -Publish: create GitHub release v<Version> with
  the zip, then commit and push manifest.json so servers see the update.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$Changelog,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
$git = 'C:\Program Files\Git\cmd\git.exe'
$repo = 'AHouseOfBards/BetterPlay'
$targetAbi = '12.0.0.0'
$guid = '575fdaea-52dd-4363-a84b-69c5c52daded'

$out = Join-Path $PSScriptRoot "artifacts\$Version"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
& $dotnet publish Jellyfin.Plugin.BetterPlay -c Release -o "$out\publish" "-p:Version=$Version" -nologo
if ($LASTEXITCODE) { throw "build failed" }

$zip = Join-Path $out "betterplay_$Version.zip"
Compress-Archive -Path "$out\publish\Jellyfin.Plugin.BetterPlay.dll" -DestinationPath $zip -Force
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()
"zip: $zip  md5: $md5"

$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$manifest = @(Get-Content $manifestPath -Raw | ConvertFrom-Json)
$plugin = $manifest | Where-Object guid -eq $guid
$entry = [pscustomobject]@{
    version   = $Version
    changelog = $Changelog
    targetAbi = $targetAbi
    sourceUrl = "https://github.com/$repo/releases/download/v$Version/betterplay_$Version.zip"
    checksum  = $md5
    timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$plugin.versions = @($entry) + @($plugin.versions | Where-Object version -ne $Version)
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject @($manifest) -Depth 6), (New-Object Text.UTF8Encoding $false))
"manifest.json updated ($(@($plugin.versions).Count) version(s))"

if ($Publish) {
    & $gh release create "v$Version" $zip --repo $repo --title "BetterPlay $Version" --notes $Changelog
    if ($LASTEXITCODE) { throw "release failed" }
    & $git add manifest.json
    & $git commit -m "Release $Version"
    & $git push
    "Published. Servers pick it up from https://raw.githubusercontent.com/$repo/main/manifest.json"
}
