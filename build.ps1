# Compila o ModoGame.exe com o compilador C# que já vem no Windows (.NET Framework 4.8)
param([switch]$Teste)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
if (-not (Test-Path "$root\src\icon.ico")) { & "$root\src\icon.ps1" -Out "$root\src\icon.ico" }
$out = if ($Teste) { "$root\ModoGame-teste.exe" } else { "$root\ModoGame.exe" }
$cargs = @('/nologo', '/target:winexe', '/optimize+', "/out:$out", "/win32icon:$root\src\icon.ico",
  "/r:$wpf\PresentationFramework.dll", "/r:$wpf\PresentationCore.dll", "/r:$wpf\WindowsBase.dll",
  "/r:$fw\System.Xaml.dll", '/r:System.Core.dll',
  "/resource:$root\src\ui.xaml,ui.xaml")
if (-not $Teste) { $cargs += "/win32manifest:$root\src\app.manifest" }
$cargs += "$root\src\Engine.cs", "$root\src\Ui.cs"
& "$fw\csc.exe" @cargs
if ($LASTEXITCODE -eq 0) { Write-Host "OK -> $out" }
