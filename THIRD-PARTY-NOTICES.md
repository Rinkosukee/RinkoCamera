# Third-party notices

RinkoCamera retains the license and copyright notices of its dependencies in
`licenses/`. The table below is generated from the restored application graph;
it includes transitive packages, some of which may be replaced by runtime copies
when publishing a self-contained build. Test-only tools are restored by NuGet
and are not shipped in the application.

| Package / version | License |
| --- | --- |
| CommunityToolkit.Mvvm/8.4.0 | MIT |
| Microsoft.NETCore.Platforms/3.1.0 | MIT |
| Microsoft.Win32.Registry/4.7.0 | MIT |
| Microsoft.Win32.SystemEvents/8.0.0 | MIT |
| NAudio.Asio/2.2.1 | MIT |
| NAudio.Core/2.2.1 | MIT |
| NAudio.Midi/2.2.1 | MIT |
| NAudio.Wasapi/2.2.1 | MIT |
| NAudio.WinForms/2.2.1 | MIT |
| NAudio.WinMM/2.2.1 | MIT |
| NAudio/2.2.1 | MIT (license file retained) |
| SharpGen.Runtime.COM/2.4.2-beta | MIT |
| SharpGen.Runtime/2.4.2-beta | MIT |
| System.Drawing.Common/8.0.19 | MIT |
| System.IO.Pipelines/9.0.1 | MIT |
| System.Security.AccessControl/4.7.0 | MIT |
| System.Security.Principal.Windows/4.7.0 | MIT |
| System.Text.Encodings.Web/9.0.1 | MIT |
| System.Text.Json/9.0.1 | MIT |
| Vortice.Direct3D11/3.8.3 | MIT |
| Vortice.DirectX/3.8.3 | MIT |
| Vortice.DXGI/3.8.3 | MIT |
| Vortice.Mathematics/2.1.0 | MIT |
| Vortice.MediaFoundation/3.8.3 | MIT |

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
