# Architecture notes

The application is split into three projects:

- `LocalRecorder.Core` contains state, options, contracts, and deterministic validation.
- `LocalRecorder.Infrastructure` contains Windows Graphics Capture target discovery, the frame source, NAudio WASAPI capture, the Media Foundation sink writer, storage, JSON settings, logging, and `RegisterHotKey`.
- `LocalRecorder` contains the WPF shell and MVVM Toolkit view model.

The controller owns the state transitions `Idle`, `Preparing`, `Recording`, `Paused`, `Stopping`, and `Error`. It creates a linked cancellation token for each run, rejects repeated starts, and always finalizes the encoder before committing a file. UI updates are dispatched back to WPF.

## Windows capture boundary

`GraphicsCaptureSession.IsSupported()` is checked before capture. `GraphicsCaptureInterop` creates items through the native `IGraphicsCaptureItemInterop.CreateForWindow/CreateForMonitor` ABI with HWND/HMONITOR. A raw HWND must not be cast to `Windows.UI.WindowId`. Surface extraction uses the native WinRT ABI followed by `QueryInterface(IDirect3DDxgiInterfaceAccess)`; `Marshal.GetIUnknownForObject` would create a managed CCW that cannot expose the texture. The WPF control window remains visible throughout capture. Windows 10 1903 (18362) is the minimum desktop capture API version; the current project targets the 19041 SDK contract. Known display affinity protection and minimized targets fail clearly. DRM or application capture restrictions are not bypassed, and arbitrary black frames alone cannot reliably prove DRM.

Window, monitor, and primary-display full-screen targets share `Direct3D11CaptureFramePool` and a three-frame bounded queue. A staging texture is reused until its dimensions change. Frame-pool recreation handles window resizing; static windows reuse the last pixel buffer with fresh timestamps so the output duration remains correct. Full-screen means the primary HMONITOR. Window capture never silently falls back to a screen-region GDI copy. Unsupported WGC permits a clearly reported display compatibility path. High-resolution 60 FPS still needs a validated GPU NV12 converter and zero-copy MF sample path.

The capture queue has a small upper bound. Old frames are disposed and counted when the producer outruns the encoder. The controller records captured, encoded, dropped, and audio sample counts in `RecordingResult.Metrics`. Audio endpoint state is polled while recording; device faults cancel the run, serialize finalization with the last audio callback, and preserve the encoded prefix when possible.

WASAPI shared mode requests 48 kHz stereo IEEE float with Windows' automatic conversion/resampling. `PcmAudio` handles float/extensible and PCM16/24/32 explicitly, rejecting unknown formats or rates instead of generating silent samples with false metadata. Each source has a bounded buffer. A timed worker mixes 10 ms PCM16 blocks, supplying zeros for an inactive endpoint; one silent endpoint never blocks the other. Video and audio start together after the first captured frame, and the media clock excludes pauses. A `SemaphoreSlim` serializes audio writes with asynchronous MP4 finalization. The UI receives throttled peak updates, and a completely silent recording produces a notice. Finalize failures and cancellation before the first frame do not commit corrupt/empty files.

## Encoding boundary

`MediaFoundationMp4Encoder` uses `IMFSinkWriter` and the system H.264/AAC MFTs. `VideoFrame` contains top-down BGRA32 rows with a positive source stride. The encoder copies rows in reverse order directly into its MF buffer and declares a negative RGB32 default stride, matching MF's bottom-up system-memory RGB convention without allocating another bitmap. Padded input rows are supported; invalid sizes/strides fail explicitly. Input audio is 48 kHz stereo PCM. Startup probes `mfplat.dll`, `mfreadwrite.dll`, and the media type projections. Sink Writer requests hardware transforms; Windows may negotiate a software MFT when hardware encoding is unavailable. The temporary filename remains `.tmp.mp4` so Media Foundation can infer the MP4 container; storage renames it to the final `.mp4` only after `Finalize` succeeds.

While `Preparing`, the controller emits `CountdownChanged` once per second. WPF dispatches the remaining value to a visible numeric indicator. Encoding and audio begin after the last delay. `Preparing -> Stopping` supports cancellation, and a cancelled countdown creates no encoder output.

No administrator permission, network call, upload, browser recorder, or telemetry is needed.
