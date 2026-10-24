param([Parameter(Mandatory=$true)][string]$Endpoint, [Parameter(Mandatory=$true)][string]$DataDir)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$testOut = Join-Path $PSScriptRoot 'bin/Tests.exe'
$testArgs = @('/nologo','/target:exe','/optimize+','/utf8output',('/out:' + $testOut))
foreach ($reference in @('System.dll','System.Core.dll','System.Security.dll','System.Web.Extensions.dll','System.Net.Http.dll')) { $testArgs += '/reference:' + (Join-Path $framework $reference) }
$testArgs += (Join-Path $PSScriptRoot 'Core.cs'), (Join-Path $PSScriptRoot 'Tests.cs')
& (Join-Path $framework 'csc.exe') @testArgs
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testOut $Endpoint $DataDir
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
