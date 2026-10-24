param([Parameter(Mandatory=$true)][string]$Endpoint, [Parameter(Mandatory=$true)][string]$DataDir)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$testOut = Join-Path $PSScriptRoot ('bin/UiTests-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.exe')
$testArgs = @('/nologo','/target:exe','/main:LingoFloat.UiTests','/optimize+','/utf8output',('/out:' + $testOut))
foreach ($reference in @('System.dll','System.Core.dll','System.Security.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Xaml.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll')) { $testArgs += '/reference:' + (Join-Path $framework $reference) }
foreach ($name in @('Main','Settings')) { $testArgs += '/resource:' + (Join-Path $PSScriptRoot ($name + '.xaml')) + ',LingoFloat.' + $name + '.xaml' }
$testArgs += (Join-Path $PSScriptRoot 'Core.cs'), (Join-Path $PSScriptRoot 'App.cs'), (Join-Path $PSScriptRoot 'UiTests.cs')
& (Join-Path $framework 'csc.exe') @testArgs
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
& $testOut $Endpoint $DataDir
if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
