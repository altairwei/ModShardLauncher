param([string]$Configuration = "Debug")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$msbuild = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

dotnet build "$root\MslLive.Shared\MslLive.Shared.csproj" -c $Configuration -v q
dotnet publish "$root\MslLive.Agent\MslLive.Agent.csproj" -c $Configuration -v q
& $msbuild "$root\MslLive.Bootstrap\MslLive.Bootstrap.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:q

$out = "$root\bin\$Configuration\net6.0-windows\msllive-runtime"
New-Item -ItemType Directory -Force "$out\msllive" | Out-Null
Copy-Item "$root\MslLive.Bootstrap\x64\Release\version.dll" $out -Force
Copy-Item "$root\MslLive.Agent\bin\$Configuration\net6.0\publish\*" "$out\msllive" -Recurse -Force
Write-Host "msllive-runtime assembled at $out"
