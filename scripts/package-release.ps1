param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist'))
if (-not $distRoot.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Distribution path escaped the repository.'
}

if (Test-Path -LiteralPath $distRoot) {
    $resolved = (Resolve-Path -LiteralPath $distRoot).Path
    if ($resolved -ne $distRoot -or (Split-Path -Leaf $resolved) -ne 'dist') {
        throw "Refusing to clear unexpected distribution path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$selfContained = Join-Path $distRoot 'win-x64'
$frameworkDependent = Join-Path $distRoot 'win-x64-framework-dependent'
New-Item -ItemType Directory -Force -Path $selfContained, $frameworkDependent | Out-Null

dotnet publish (Join-Path $repoRoot 'NativeWidget\NativeWidget.csproj') -c Release -r win-x64 `
    --self-contained true -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $selfContained
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

dotnet publish (Join-Path $repoRoot 'NativeWidget\NativeWidget.csproj') -c Release -r win-x64 `
    --self-contained false -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $frameworkDependent
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }

foreach ($folder in @($selfContained, $frameworkDependent)) {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $folder
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $folder
}

$prefix = "Native-Widget-v$Version"
$selfContainedZip = Join-Path $distRoot "$prefix-win-x64.zip"
$frameworkZip = Join-Path $distRoot "$prefix-win-x64-framework-dependent.zip"
Compress-Archive -Path (Join-Path $selfContained '*') -DestinationPath $selfContainedZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $frameworkDependent '*') -DestinationPath $frameworkZip -CompressionLevel Optimal

$checksums = @($selfContainedZip, $frameworkZip) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
}
Set-Content -LiteralPath (Join-Path $distRoot 'SHA256SUMS.txt') -Value $checksums -Encoding utf8

Get-Item -LiteralPath $selfContainedZip, $frameworkZip, (Join-Path $distRoot 'SHA256SUMS.txt') |
    Select-Object Name, Length
