# Contributing to RinkoCamera

Use Windows 10 version 2004 or later / Windows 11 x64 and the .NET SDK specified
in global.json. Restore and build `RinkoCamera.sln`; run `dotnet test RinkoCamera.sln -c Release`.
An interactive desktop and Media Foundation H.264/AAC encoders are needed for the
real media tests. Set `LOCALRECORDER_DESKTOP_TESTS=1` to opt in to the capture/audio
tests; this historic environment variable name is retained for compatibility.

Keep changes small, explain the problem, and include relevant validation. Never
attach credentials, personal recordings, device identifiers, or settings files to
an issue or pull request. Report playback failures with non-sensitive reproduction
steps and the Windows/build version.

New application code and documentation contributions are under MIT. Retain
third-party notices. TSUKI artwork has separate terms in ARTWORK-LICENSE.md.

Hosted CI excludes tests with the WindowsMediaIntegration category and does not
enable optional desktop hardware tests. A passing CI run is not proof of 60 FPS,
audio device behavior, or successful installer upgrades on a clean Windows VM.
