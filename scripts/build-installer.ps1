param(
    [string]$Configuration = "Release",
    [string]$PublishOutput = "artifacts\publish\RinkoCamera\win-x64",
    [string]$Output = "artifacts\installer",
    [string]$Version = "1.0.7",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishPath = [System.IO.Path]::GetFullPath((Join-Path $root $PublishOutput))
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $root $Output))
$installerPath = Join-Path $root "installer"
$generatedPath = Join-Path $outputPath "generated-components.wxs"
$msiPath = Join-Path $outputPath "RinkoCamera-$Version-win-x64.msi"
$bundlePath = Join-Path $outputPath "RinkoCamera-$Version-Setup.exe"
$brandPath = Join-Path $root "src\RinkoCamera\Assets"

function Get-WixPath {
    $command = Get-Command wix -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    Write-Host "WiX v5 was not found. Installing the wix dotnet tool..."
    dotnet tool install --global wix --version 5.0.2
    $command = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $command) {
        $toolPath = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
        if (Test-Path $toolPath) { return $toolPath }
        throw "WiX v5 installation completed but wix.exe was not found on PATH."
    }
    return $command.Source
}

if (-not $SkipPublish -or -not (Test-Path (Join-Path $publishPath "RinkoCamera.exe"))) {
    & (Join-Path $PSScriptRoot "publish.ps1") -Configuration $Configuration -Output $PublishOutput
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
$publishedVersion = (Get-Item -LiteralPath (Join-Path $publishPath 'RinkoCamera.exe')).VersionInfo.ProductVersion
if (($publishedVersion -split '\+')[0] -ne $Version) { throw "Published app version $publishedVersion does not match installer version $Version." }

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$wixSource = Join-Path $installerPath "RinkoCamera.wxs"
if (-not (Test-Path $wixSource)) { throw "WiX package source is missing: $wixSource" }

function ConvertTo-XmlAttribute([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

function Get-StableGuid([string]$relativePath) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(('RinkoCamera/' + $relativePath).ToLowerInvariant())
        $hash = $md5.ComputeHash($bytes)
        return ([Guid]::new($hash)).ToString().ToUpperInvariant()
    } finally { $md5.Dispose() }
}

$files = Get-ChildItem -Path $publishPath -File -Recurse | Sort-Object FullName
$directoryMap = @{ "." = "INSTALLFOLDER" }
$directoryRows = [System.Collections.Generic.List[object]]::new()

function Get-DirectoryId([string]$relativeDirectory) {
    if ([string]::IsNullOrWhiteSpace($relativeDirectory) -or $relativeDirectory -eq ".") {
        return "INSTALLFOLDER"
    }
    if ($directoryMap.ContainsKey($relativeDirectory)) {
        return $directoryMap[$relativeDirectory]
    }
    $parent = Split-Path $relativeDirectory -Parent
    if ([string]::IsNullOrWhiteSpace($parent)) { $parent = "." }
    # Ensure parent nodes are emitted before children, which is required by
    # the WiX directory tree and also keeps the generated source deterministic.
    $parentId = Get-DirectoryId $parent
    $id = "dir" + ($directoryMap.Count).ToString("D4")
    $name = Split-Path $relativeDirectory -Leaf
    $directoryMap[$relativeDirectory] = $id
    $directoryRows.Add([pscustomobject]@{ Relative = $relativeDirectory; ParentId = $parentId; Id = $id; Name = $name })
    return $id
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('<?xml version="1.0" encoding="UTF-8"?>')
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$directoryRowsByPath = @{}
foreach ($file in $files) {
    if ($file.Name -ieq "RinkoCamera.exe") { continue }
    $relativePath = $file.FullName.Substring($publishPath.Length).TrimStart('\', '/')
    $relativeDirectory = Split-Path $relativePath -Parent
    if ([string]::IsNullOrWhiteSpace($relativeDirectory)) { $relativeDirectory = "." }
    [void](Get-DirectoryId $relativeDirectory)
}

# Keep every published subdirectory (satellite resource folders in particular)
# so MSI does not flatten files with the same name into one directory.
$directoryRows | ForEach-Object {
    $lines.Add('  <Fragment>')
    $lines.Add(('    <DirectoryRef Id="{0}">' -f $_.ParentId))
    $lines.Add(('      <Directory Id="{0}" Name="{1}" />' -f $_.Id, (ConvertTo-XmlAttribute $_.Name)))
    $lines.Add('    </DirectoryRef>')
    $lines.Add('  </Fragment>')
}

$lines.Add('  <Fragment>')
$lines.Add('    <ComponentGroup Id="AppFiles">')
$index = 0
$removeFolderAdded = @{}
foreach ($file in $files) {
    # The shortcut component owns the exe so that the shortcut is advertised
    # and removed correctly. It is intentionally omitted from this harvest.
    if ($file.Name -ieq "RinkoCamera.exe") { continue }
    $index++
    # Windows PowerShell 5.1 runs on the .NET Framework where
    # Path.GetRelativePath is unavailable; both paths share publishPath as a
    # canonical prefix, so a normalized substring is deterministic here.
    $relative = $file.FullName.Substring($publishPath.Length).TrimStart('\', '/')
    $relativeDirectory = Split-Path $relative -Parent
    if ([string]::IsNullOrWhiteSpace($relativeDirectory)) { $relativeDirectory = "." }
    $directoryId = Get-DirectoryId $relativeDirectory
    $source = "`$(var.PublishDir)\$relative"
    $id = "cmp$index"
    $fileId = "fil$index"
    $guid = Get-StableGuid $relative
    $lines.Add(('      <Component Id="{0}" Directory="{1}" Guid="{2}">' -f $id, $directoryId, $guid))
    $lines.Add(('        <File Id="{0}" Source="{1}" KeyPath="no" />' -f $fileId, (ConvertTo-XmlAttribute $source)))
    if (-not $removeFolderAdded.ContainsKey($directoryId)) {
        $removeFolderAdded[$directoryId] = $true
        $removeId = "Remove" + $directoryId
        $lines.Add(('        <RemoveFolder Id="{0}" On="uninstall" />' -f $removeId))
    }
    $lines.Add(('        <RegistryValue Root="HKCU" Key="Software\RinkoCamera\Installer\Files" Name="{0}" Type="integer" Value="1" KeyPath="yes" />' -f $id))
    $lines.Add('      </Component>')
}
$lines.Add('    </ComponentGroup>')
$lines.Add('  </Fragment>')
$lines.Add('</Wix>')
[System.IO.File]::WriteAllLines($generatedPath, $lines, [Text.UTF8Encoding]::new($false))

$wix = Get-WixPath
Write-Host "Building MSI with WiX: $wix"
& $wix build $wixSource $generatedPath -arch x64 -d "PublishDir=$publishPath" -d "ProductVersion=$Version" -d "BrandDir=$brandPath" -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "WiX build failed with exit code $LASTEXITCODE" }

Write-Host "MSI created: $msiPath"

# WiX extensions are cached in this repository, rather than installed into the OS.
Push-Location $root
try {
    $extensionId = "WixToolset.BootstrapperApplications.wixext/5.0.2"
    $extensionDll = Join-Path $root ".wix\extensions\WixToolset.BootstrapperApplications.wixext\5.0.2\wixext5\WixToolset.BootstrapperApplications.wixext.dll"
    if (-not (Test-Path -LiteralPath $extensionDll)) {
        & $wix extension add $extensionId
        if ($LASTEXITCODE -ne 0) { throw "WiX BootstrapperApplications extension acquisition failed." }
    }
    & $wix build (Join-Path $installerPath "Bundle.wxs") -arch x64 -ext $extensionId -d "MsiPath=$msiPath" -d "ProductVersion=$Version" -d "BrandDir=$brandPath" -o $bundlePath
    if ($LASTEXITCODE -ne 0) { throw "WiX bundle build failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }
Write-Host "Branded offline setup created: $bundlePath"
