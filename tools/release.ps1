# Builds the two release downloads into dist\:
#   AutoMonitorBrightness.exe             small; needs the .NET 10 Desktop Runtime
#   AutoMonitorBrightness-standalone.exe  larger; runs on any Windows 10/11 x64 PC with nothing else installed
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$common = @('-c', 'Release', '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:DebugType=none', '-nologo')

dotnet publish "$root\AutoMonitorBrightness.csproj" @common --self-contained false -o "$dist\small"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
dotnet publish "$root\AutoMonitorBrightness.csproj" @common --self-contained true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$dist\standalone"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Copy-Item "$dist\small\AutoMonitorBrightness.exe" "$dist\AutoMonitorBrightness.exe"
Copy-Item "$dist\standalone\AutoMonitorBrightness.exe" "$dist\AutoMonitorBrightness-standalone.exe"
Remove-Item "$dist\small", "$dist\standalone" -Recurse -Force

Get-ChildItem $dist -Filter *.exe | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "{0}  {1}" -f $hash, $_.Name
} | Set-Content "$dist\SHA256SUMS.txt" -Encoding ascii
Get-ChildItem $dist | Select-Object Name, @{ n = 'Size'; e = { '{0:N0} KB' -f ($_.Length / 1KB) } }
