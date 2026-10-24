param([string]$OutputDir)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$framework = Split-Path -Parent $compiler
$buildOut = if ([String]::IsNullOrWhiteSpace($OutputDir)) { Join-Path $PSScriptRoot 'bin' } else { [IO.Path]::GetFullPath($OutputDir) }
New-Item -ItemType Directory -Path $buildOut -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Security.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Xaml.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:winexe','/optimize+','/platform:anycpu','/utf8output',('/out:' + (Join-Path $buildOut 'LingoFloat.exe')),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $framework $reference) }
$icon = Join-Path $PSScriptRoot 'app.ico'
if (Test-Path -LiteralPath $icon) { $compilerArgs += '/win32icon:' + $icon }
$compilerArgs += '/resource:' + (Join-Path $PSScriptRoot 'Main.xaml') + ',LingoFloat.Main.xaml'
$compilerArgs += '/resource:' + (Join-Path $PSScriptRoot 'Settings.xaml') + ',LingoFloat.Settings.xaml'
$compilerArgs += (Join-Path $PSScriptRoot 'Core.cs'), (Join-Path $PSScriptRoot 'App.cs')
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LingoFloat.exe.config') -Destination $buildOut -Force
Write-Output (Join-Path $buildOut 'LingoFloat.exe')
