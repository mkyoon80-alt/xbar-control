$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$output = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $framework 'csc.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output',
    "/out:$output\XbarControl.exe", "/win32manifest:$projectRoot\src\app.manifest",
    "/resource:$projectRoot\src\MainWindow.xaml,MainWindow.xaml",
    "/resource:$projectRoot\THIRD-PARTY-NOTICES.txt,ThirdPartyLicense.txt",
    "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\PresentationFramework.dll",
    "/reference:$wpf\WindowsBase.dll", '/reference:System.Xaml.dll', '/reference:System.Core.dll',
    '/reference:System.Runtime.Serialization.dll',
    "$projectRoot\src\NvApi.cs", "$projectRoot\src\App.cs", "$projectRoot\src\Theme.cs", "$projectRoot\src\SelfTests.cs")
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $output\XbarControl.exe"
