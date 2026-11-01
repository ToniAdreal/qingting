$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName
$appPath = Join-Path $PSScriptRoot 'SpaceDictation.exe'
$uiaPath = Join-Path (Split-Path $compilerPath) 'WPF'
$iconPath = Join-Path $PSScriptRoot 'assets\qingting.ico'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$appPath" "/win32icon:$iconPath" "/lib:$uiaPath" /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Security.dll /r:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Get-Item -LiteralPath $appPath | Select-Object FullName, Length
