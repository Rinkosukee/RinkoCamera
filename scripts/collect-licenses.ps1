param([string]$RuntimeVersion = '8.0.32', [string]$WindowsSdkVersion = '10.0.26100.86')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assetsPath = Join-Path $root 'src\RinkoCamera\obj\project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath)) { throw 'Run dotnet restore RinkoCamera.sln first.' }
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$cache = ($assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name
$licenseRoot = Join-Path $root 'licenses'
$rows = [Collections.Generic.List[string]]::new()
foreach ($library in ($assets.libraries.PSObject.Properties | Sort-Object Name)) {
    if ($library.Value.type -ne 'package') { continue }
    $packagePath = Join-Path $cache $library.Value.path
    $nuspec = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' | Select-Object -First 1
    [xml]$spec = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $spec.package.metadata
    $packageId = [string]$metadata.id
    $destination = Join-Path $licenseRoot ('nuget\' + $library.Value.path)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $noticeFiles = @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '(?i)license|notice|copyright' })
    foreach ($notice in $noticeFiles) { Copy-Item -LiteralPath $notice.FullName -Destination $destination -Force }
    if ($noticeFiles.Count -eq 0) {
        $fallback = switch -Wildcard ($packageId) {
            'NAudio.*' { Join-Path $cache 'naudio\2.2.1\license.txt'; break }
            'Vortice.Mathematics' { Join-Path $licenseRoot 'upstream\Vortice.Mathematics.txt'; break }
            'Vortice.*' { Join-Path $licenseRoot 'upstream\Vortice.Windows.txt'; break }
            'SharpGen.*' { Join-Path $licenseRoot 'upstream\SharpGenTools.txt'; break }
            default { throw "No full license notice found for $packageId. Review before distributing." }
        }
        Copy-Item -LiteralPath $fallback -Destination (Join-Path $destination 'LICENSE.txt') -Force
    }
    $license = if ($metadata.license.type -eq 'file') { 'MIT (license file retained)' } else { [string]$metadata.license.InnerText }
    $rows.Add('| ' + $library.Name + ' | ' + $license + ' |')
}
foreach ($runtime in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $runtimePath = Join-Path $cache ($runtime + '\' + $RuntimeVersion)
    $destination = Join-Path $licenseRoot ($runtime + '-' + $RuntimeVersion)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($notice in (Get-ChildItem -LiteralPath $runtimePath -File | Where-Object { $_.Name -match '(?i)license|notice' })) {
        Copy-Item -LiteralPath $notice.FullName -Destination $destination -Force
    }
}
$sdkPath = Join-Path $cache ('microsoft.windows.sdk.net.ref\' + $WindowsSdkVersion + '\microsoft.windows.sdk.net.ref.nuspec')
Copy-Item -LiteralPath $sdkPath -Destination (Join-Path $licenseRoot 'WindowsSDK-NuGet.nuspec') -Force
$text = @'
# Third-party notices

RinkoCamera retains the license and copyright notices of its dependencies in
`licenses/`. The table below is generated from the restored application graph;
it includes transitive packages, some of which may be replaced by runtime copies
when publishing a self-contained build. Test-only tools are restored by NuGet
and are not shipped in the application.

| Package / version | License |
| --- | --- |
'@
$text += "`n" + ($rows -join "`n") + "`n"
$text += @'

The self-contained win-x64 build includes .NET and Windows Desktop runtime packs.
Their supplied license notices are retained separately under `licenses/`.
Windows SDK projection assemblies use the [Microsoft Windows SDK terms](https://aka.ms/WinSDKLicenseURL);
the SDK's package metadata is retained in `licenses/WindowsSDK-NuGet.nuspec`.
The C#/WinRT runtime notice is in `licenses/upstream/CsWinRT.txt`.
Windows.Graphics.Capture, Direct3D11, and Media Foundation OS components are
provided by Windows. This project does not redistribute Windows itself.

The Setup executable contains unchanged WiX v5.0.2 Burn / standard bootstrapper
components under MS-RL. The full license is in `licenses/upstream/WiX-v5.0.2.txt`;
corresponding upstream source is supplied as `wix-v5.0.2-source.tar.gz` alongside
the Setup in the GitHub Release. Original source and build instructions:
https://github.com/wixtoolset/wix/tree/v5.0.2 . RinkoCamera's application source
files are separately licensed under MIT.

FFmpeg and third-party codec binaries are not bundled. MP4 uses Windows' H.264
and AAC encoders. TSUKI artwork is covered by ARTWORK-LICENSE.md, not MIT.
'@
[IO.File]::WriteAllText((Join-Path $root 'THIRD-PARTY-NOTICES.md'), $text, [Text.UTF8Encoding]::new($false))
Write-Host ('Collected notices for ' + $rows.Count + ' restored packages and runtime packs.')
