param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ProjectDir,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$stagingRoot = Join-Path $ProjectDir 'obj\_module_staging'
$versionDir = Join-Path $stagingRoot $Version
$clientResourcesSrc = Join-Path $ProjectDir 'ClientResources'
$clientResourcesDst = Join-Path $versionDir 'ClientResources'
if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
  $OutputPath = Join-Path $ProjectDir $OutputPath
}

if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }
New-Item $clientResourcesDst -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $clientResourcesSrc '*') $clientResourcesDst -Recurse -Force

$moduleConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<module name="Cheapskate.AITranslator" clientResourceRelativePath="$Version">
  <assemblies>
    <add assembly="Cheapskate.AITranslator" />
  </assemblies>
  <dojo>
    <packages>
      <add name="cheapskate" location="ClientResources" />
    </packages>
  </dojo>
  <clientModule initializer="cheapskate/LanguageVersionCreatorInitializer">
    <moduleDependencies>
      <add dependency="Shell" />
      <add dependency="CMS" type="RunAfter" />
    </moduleDependencies>
  </clientModule>
</module>
"@
$moduleConfig | Set-Content (Join-Path $stagingRoot 'module.config') -Encoding UTF8

if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }
Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $OutputPath -Force
Write-Host "Cheapskate.AITranslator.zip created at $OutputPath (version $Version)"
