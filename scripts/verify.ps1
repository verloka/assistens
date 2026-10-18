# Windows PowerShell 5.1+; no modules required.
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Invoke-DotNet([string[]]$Arguments) {
  & dotnet @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw ('dotnet ' + ($Arguments -join ' ') + ' failed with exit code ' + $LASTEXITCODE)
  }
}

Push-Location -LiteralPath $root
try {
  Invoke-DotNet @('restore', 'PersonalAgent.sln', '--locked-mode')
  Invoke-DotNet @('build', 'PersonalAgent.sln', '--no-restore', '--configuration', $Configuration)
  Invoke-DotNet @('test', 'PersonalAgent.sln', '--no-build', '--configuration', $Configuration)
} finally {
  Pop-Location
}
