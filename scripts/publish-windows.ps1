# Windows PowerShell 5.1+; local build/diagnostic only, no installation or startup registration.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src/PersonalAgent.App/PersonalAgent.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$name = 'PersonalAgent-' + $version + '-win-x64'
$buildRoot = Join-Path $root ('artifacts/packages/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $buildRoot $name
New-Item -ItemType Directory -Path $destination | Out-Null

& dotnet publish $project -c Release -p:RestoreLockedMode=true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
foreach ($file in @('PersonalAgent.App.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll', 'e_sqlite3.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $file) -PathType Leaf)) { throw ('Missing bundled component: ' + $file) }
}
$runtime = Get-Content -LiteralPath (Join-Path $destination 'PersonalAgent.App.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) {
    throw 'Runtime configuration is not self-contained.'
}
Copy-Item -LiteralPath (Join-Path $root 'docs/DISTRIBUTION.md') -Destination (Join-Path $destination 'README.md')

# Run only the explicit isolated smoke mode, with installed-runtime roots unavailable.
$emptyRuntime = Join-Path $buildRoot 'empty-runtime'
New-Item -ItemType Directory -Path $emptyRuntime | Out-Null
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = Join-Path $destination 'PersonalAgent.App.exe'
$start.Arguments = '--package-smoke'
$start.WorkingDirectory = $destination
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.EnvironmentVariables['DOTNET_ROOT'] = $emptyRuntime
$start.EnvironmentVariables['DOTNET_ROOT_X64'] = $emptyRuntime
$start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$start.EnvironmentVariables['DOTNET_HOST_TRACE'] = '1'
$start.EnvironmentVariables['DOTNET_HOST_TRACEFILE'] = Join-Path $buildRoot 'package-host.trace.log'
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Package diagnostic could not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(45000)) { $process.Kill(); $process.WaitForExit(); throw 'Package diagnostic timed out.' }
    $report = $stdout.GetAwaiter().GetResult()
    $errorReport = $stderr.GetAwaiter().GetResult()
    $report | Set-Content -LiteralPath (Join-Path $buildRoot 'package-smoke.txt') -Encoding utf8
    if ($process.ExitCode -ne 0 -or -not $report.Contains('PACKAGE SMOKE OK;') -or $errorReport.Length -gt 0) {
        throw ('Package diagnostic failed; exit code ' + $process.ExitCode + '. See local package-smoke.txt/host trace.')
    }
    Write-Output $report.Trim()
} finally { $process.Dispose() }

$archive = Join-Path $buildRoot ($name + '.zip')
Compress-Archive -Path $destination -DestinationPath $archive -CompressionLevel Optimal
$digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
($digest + '  ' + [IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
Write-Output ('ZIP: ' + $archive)
Write-Output ('SHA256: ' + $digest)
