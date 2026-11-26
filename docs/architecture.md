# Architecture

## Goals

- Sign in with a PSN account, register a PS5 on the local network, stream and play.
- Low latency, correct rendering, configurable quality (resolution, fps, codec, HDR, bitrate).
- Windows and Linux as first-class platforms. macOS should not be blocked by design choices.

## Non-goals (for now)

PS4 consoles, remote play over the internet through Sony's servers. Playing from another
network is supported only through [console sharing](#console-sharing), where a computer on
the console's network relays the traffic.

## Technology choices

| Concern | Choice | Reason |
|---|---|---|
| Runtime | .NET 10 (LTS) | Current LTS, good `Span`/SIMD support, AOT possible later. |
| Window, input, audio output | SDL3 (Hexa.NET.SDL3) | One cross-platform layer for windowing, gamepads (DualSense included), audio. Native SDL3 ships with the package for Windows, Linux and macOS. |
| UI | Dear ImGui (Hexa.NET.ImGui) with the SDL3 platform and D3D11 (Windows) / SDL_Renderer (Linux/macOS) backends, own theme and widgets, Inter and Lucide fonts embedded | The UI is a small launcher (setup, console cards, settings, PIN/passcode prompts). One window and one render loop for both UI and video. See [User interface](#user-interface). |
| Video decoding | FFmpeg through FFmpeg.AutoGen, D3D11VA on Windows / VAAPI on Linux with software fallback | Only realistic option for H.264/HEVC with hardware decoding on both platforms. The build bundles an LGPL FFmpeg 9 (`build/Ffmpeg.targets`); `FfmpegLibrary` falls back to searching the usual install locations and accepts only libavcodec 63. |
| Video presentation | Native Direct3D 11 `IDXGISwapChain2`/`3` (`FlipDiscard`, `BufferCount = 3`, `MaximumFrameLatency = 1`, `AllowTearing`/VRR) + HLSL compute/pixel pipeline on Windows; `SDL_Renderer` textures (NV12, IYUV, P010) on Linux/macOS | Windows: the decoder shares a multithread-protected `ID3D11Device`, keeping pictures on the GPU through direct NV12/P010 -> RGBA compute shaders, GPU optical flow Frame Generation (extrapolation and interpolation), and AMD FidelityFX CAS sharpening; triple-buffering avoids VSync starvation without extra latency. Elsewhere pictures are copied back to system memory and uploaded via SDL. The Web Share browser presenter uses WebGL2 with `preserveDrawingBuffer: true`, VBlank-aligned `requestAnimationFrame` ingestion under VSync, and GPU MAD + non-blocking `PIXEL_PACK_BUFFER` `fenceSync` cadence detection. |
| Audio decoding | Concentus (managed Opus) | No native dependency; the console sends Opus. |
| Crypto | `System.Security.Cryptography` (AES, HMAC-SHA256, SHA-256, secp256k1 ECDH) plus an own GHASH for the 16-byte-IV GMAC | Avoid native crypto dependencies. |
| Protobuf | Google.Protobuf with an own `takion.proto` subset | Stream connection messages are protobuf (proto2). |

## Projects

```
src/
  Cord.Protocol     Pure protocol: discovery, registration, session request, ctrl channel,
                      Takion transport, stream crypto, stream connection, senkusha,
                      AV packet reassembly and FEC, controller feedback encoding,
                      RemotePlaySession (the whole connection sequence).
                      Depends on the BCL and Google.Protobuf only.
  Cord.Psn          PSN OAuth sign-in and account id retrieval (HTTP only).
  Cord.Media        Video decoding (FFmpeg) and audio decoding (Opus). No windowing.
  Cord.Relay        Console relay: listens on the Remote Play ports and forwards them to
                      a console, so clients on other networks can reach it.
                      Depends on the BCL and Cord.Protocol (ports, subnet helpers) only.
  Cord.Core         Settings and quality presets, registered consoles, PSN account,
                      console browser (continuous discovery), console sharing.
  Cord.App          Executable "Cord": SDL3 window, ImGui UI, video presentation, audio
                      output, gamepad/keyboard input, stream controller.
tests/
  Cord.Protocol.Tests, Cord.Psn.Tests, Cord.Media.Tests, Cord.Relay.Tests,
  Cord.Core.Tests, Cord.App.Tests
```

Dependency direction: `App -> Core -> (Protocol, Psn, Media, Relay)`, and `Relay -> Protocol`.
`Protocol`, `Psn` and `Media` do not reference each other. Interfaces that cross boundaries
(`IStreamSink`, `IVideoReceiverSink`, `IFeedbackTransport`, `ITakionHandler`, `IConsoleRelay`)
are defined by the lower layer.

## Threading model

- Takion: one dedicated `AboveNormal`-priority receive thread per UDP socket with blocking
  receives and DSCP EF (`0xB8`) QoS marking when permitted. Packets are verified, decrypted
  and reassembled on that thread.
- Video: reassembled frames go through a bounded `ArrayPool<byte>`-backed queue
  (`PooledFrameQueue`, capacity 4, drop-oldest) to a dedicated `AboveNormal`-priority
  decoder thread registered with Windows MMCSS (`"Games"`), waking immediately on frame
  arrival instead of waiting for VBlank. Replay buffering and WebShare forwarding run on a
  separate background queue off the decode hot path. Decoded pictures are handed to the
  render thread, which always presents the newest one (and on Windows can synthesize
  intermediate frames via GPU pyramidal Optical Flow and apply AMD FidelityFX CAS).
- Audio: decoded on the receive thread and pushed into an SDL audio stream; when more than
  80 ms are queued the queue is cleared to keep latency bounded.
- Frame pacing: on Windows, `D3D11Host` presents through `FlipDiscard` with
  `MaximumFrameLatency = 1`, `AllowTearing` when VSync is off, and a 1 ms timer scope while
  streaming in low-latency mode. `FramePacer` tracks non-blocking GPU luma MAD to detect
  30 fps games carried inside a 60 Hz stream and suppress Frame Generation on scene cuts.
  Elsewhere `FrameLimiter` caps presentation at 1.05 × the display refresh rate (2 × without
  VSync) so drivers that ignore VSync cannot spin the render loop.
- Input: while waiting on DXGI's `FrameLatencyWaitableObject`, `D3D11Host` slices waits into
  4 ms intervals so SDL gamepad/keyboard input is sampled at **250 Hz** even under 60 Hz
  VSync, matching `FeedbackSender`'s 4 ms minimum send cadence (`StateMaxInterval = 200 ms`).
  The Web Share browser client (`Cord.WebShare/Assets/index.html`) likewise polls input at
  250 Hz (with immediate event flushes) and supports WebGL2 pyramidal Optical Flow Frame
  Generation with temporal EMA flow smoothing, a VSync-locked continuous interpolation pace
  (plus an optional VSync-off mode with a smoothness warning), and GPU Boost shaders.
- Gamepad feedback from the console (rumble, lightbar, adaptive triggers) is queued and
  applied on the render thread. Ending a stream discards pending feedback and resets the
  triggers on that thread; closing a gamepad also resets any active effects.
- DualSense haptics bypass Opus. A dedicated SDL stream expands 3 kHz stereo PCM to four
  channels (silent channels 1/2, actuators on 3/4) and resamples to 48 kHz. The render thread
  opens/closes the named USB audio device as the selected gamepad and stream change;
  a lock serializes these operations with PCM submission on the network thread. The input
  queue has the same 80 ms bound as game audio. Legacy rumble is suppressed while this
  output is available, because it disables the DualSense's audio haptic mode.

## User interface

The UI targets people who just want to play, so every page has one obvious action and
technical options stay behind "Customize" and "Advanced".

- `Ui/Shell` draws the window chrome (background, top bar) and switches between pages (`Guide`, `Setup`, `Home`, `Pair`, `Settings`, `Changelog`). The application version and optional GitHub repository (`CordGitHubRepo` / `CORD_GITHUB_REPO`) are defined centrally in `Directory.Build.props` and surfaced in `AppVersion`, the clickable desktop top-bar badge (with a green dot indicator when an update is available), window title, Settings footer, and Web Share HTML templates (`{{CORD_VERSION}}`):
  - **Guide** (`Ui/GuideScreen`, first run before Setup and reopenable anytime from the top bar or Settings): 5-page interactive walk-through covering PS5 Remote Play/Rest Mode setup and local privacy guarantee, PSN OAuth & PIN pairing, Play/Audio + Pad/Web Share modes, recommended quality & Frame Gen/VSync settings, and optional Telegram Bot alerts & keyboard shortcuts. Completion is stored in `settings.json` (`GuideCompleted`).
  - **Changelog & Auto-Update** (`Ui/ChangelogScreen`, `Ui/UpdatePopup`, `Updating/GitHubUpdateService`, `Updating/AppUpdater`, `Updating/UpdateManager`): polls GitHub Releases in the background (`StartBackgroundCheck` on startup, plus manual Refresh), merges live release notes ahead of embedded `AppChangelog.Entries`, shows `UpdatePopup` when a higher `SemanticVersion` is published (respecting `SkippedUpdateVersion` in `settings.json`), and on portable Windows installs downloads and swaps the `.zip` payload safely via an out-of-process PowerShell helper before relaunching `Cord.exe`.
  - **Setup** (first run): welcome, sign in, pair, quality, done. It is shown until
    `settings.json` records `SetupCompleted`; installations that already have an account and a
    paired console skip it. Pairing can be skipped and done later; "I'll do this later" skips
    the whole setup. Settings > Advanced can run it again.
  - **Home**: adaptive console cards (window and card bounds clamp cleanly to `SDL_GetDisplayUsableBounds` with responsive grid columns and heights so small screens never clip bottom controls) with three one-click actions per console: *Play* (full video+audio+input stream, or *Wake* from rest mode), *Audio* (**Audio + Pad** HDMI-companion mode with `StreamOptions.AudioOnly = true`: skips video packet decryption/decoding, runs a tight 40 ms `AudioOutput` latency budget, and polls the physical gamepad at 500 Hz / 2 ms even when unfocused or minimized to tray without keyboard interference), and *Web* (browser Web Share host). Banners report active Audio + Pad state, Web Share room/links, console sharing, and discovery notices.
  - **Pair**: sign in when needed, pick a discovered console, type the PIN. "Can't find your
    console?" searches a typed address and says what happened: still looking, found, already
    paired, or nothing answered within 6 s (with hints about the address, the VPN and sharing).
  - **Settings**: quality presets (Performance 720p/H.264/10 Mbps, Balanced 1080p/HEVC/15 Mbps,
    Quality 1080p/HEVC/30 Mbps, all 60 fps) with custom values (360p to 1080p, since the console
    rejects higher resolutions), display toggles, account, consoles, keyboard controls and
    advanced options. Settings saved by versions that offered 1440p/2160p load as 1080p.
    Disconnects caused by rejected video settings and "in use" errors are explained in plain
    words (`StreamEndMessages`).
- `Ui/StreamOverlay` draws over the video: the connecting screen, a short "Press F1" hint, the
  F1 menu (statistics, disconnect) and the console passcode prompt.
- Every way a stream ends leads back to the launcher: closing the window while connecting or
  streaming ends the stream instead of quitting (SDL's quit-on-last-window-close is turned
  off; closing the window from the launcher quits), and a stream that ends by itself (console
  disconnect, 5 s of silence, see [stream connection](protocol/09-stream-connection.md#detecting-a-lost-connection))
  releases its session immediately. The home screen then explains why, except when the user
  ended the stream.
- `Ui/Widgets` builds the controls (buttons, cards, toggles, option cards, steps, banners) on
  top of Dear ImGui items, so keyboard and gamepad navigation keep working; custom-drawn items
  draw their own focus ring. Escape or the gamepad's Circle button goes back from secondary
  pages.
- `Ui/Theme` holds colors, the type scale and the ImGui style. Sizes are in design units
  scaled by the window's content scale (`SdlHost.ContentScale`: display scale divided by pixel
  density) and updated when the window moves to a display with another scale. Text uses Inter
  with Lucide icons merged into each weight; Inter's own private-use glyphs are excluded so they
  do not hide the icons. Dear ImGui 1.92 rasterizes glyphs on demand at any size.
- Icons: `assets/logo.png` is the source artwork. `src/Cord.App/Assets/Icons` holds a square,
  rounded crop of it (tighter at 16-32 px so the mark stays legible): `cord.ico` is the
  executable icon on Windows (`ApplicationIcon`), and the embedded `cord-*.png` images become the
  window icon through `SDL_SetWindowIcon` on every platform (32 px base with 48-256 px
  alternates for high-DPI). Inside the app (top bar, welcome screen) `Ui/Logo` draws the
  transparent logo from `Assets/Images/logo-{64,128,256,512}.png`, picking the smallest texture
  at least as tall as the target because SDL_Renderer textures have no mipmaps. The README
  shows `assets/logo-no-bg.png`, the transparent source those images are made from (trimmed,
  without the faint haze left by its background removal).
- PSN sign-in ends on a redirect page the app cannot intercept: the user copies its address
  and the app picks it up from the clipboard (or a "Paste address" button).

## Console sharing

Lets a friend on another network play on this user's console without opening router ports.
Both computers join the same VPN (Radmin VPN, Tailscale, ZeroTier...), and the sharing
computer, which is on the console's network, relays the Remote Play ports. The friend's
Cord adds the sharing computer's VPN address with "Can't find your console?" and from then
on treats it as the console: pairing, wake-up and streaming all go through it. Nothing is
decoded or re-encoded; the stream stays encrypted end to end between the friend's Cord and
the console.

- `Cord.Relay/ConsoleRelay` listens on all Remote Play ports (see the
  [port table](protocol/README.md#connection-overview)) and forwards each to the same port of
  the console. Every exchange is started by the client and the console answers the address it
  came from, so no protocol message has to be rewritten.
- `UdpRelay` gives every client address its own upstream socket, so the console's replies go
  back to the client that caused them, and drops replies that do not come from the console.
  A client that sent or received nothing for 30 s is forgotten. On port 9295 every datagram
  is also sent to the console subnet's broadcast address, because a console in pairing mode
  answers only broadcast `SRC3` searches ([registration](protocol/04-registration.md)).
- `TcpRelay` opens one console connection per accepted client and copies bytes both ways,
  passing half-closes on, until both sides have closed.
- `RelayClientFilter` serves only clients outside the console's network that are not this
  computer, so sharing cannot be tried with a second Cord on the sharing computer (it would
  also share the same data folder and see the console as already paired). Relaying discovery broadcasts from the home network, or this computer's own
  searches on its VPN interface, would make the console appear at two addresses. The
  addresses shown to the user are this computer's IPv4 addresses outside the console's
  network, without link-local and host-only adapters (Hyper-V/WSL, Docker), which another
  computer can never reach.
- `Cord.Core/ConsoleSharing` shares one console at a time (the ports are fixed), restarts
  the relay when the console's address changes and stops it when the console is forgotten.
  Sharing is not saved: it ends when Cord closes.
- The console sends its large video packets only to the relay, over the home network; the
  relay sends them on through ordinary sockets, which the operating system can fragment if
  the VPN's MTU is smaller. The client still reports the fixed MTU described in
  [senkusha](protocol/10-senkusha.md).
- Windows Defender Firewall may ask whether Cord can accept connections. VPN adapters are
  often classified as public networks, so access must be allowed there too.

Verified against a PS5 through the relay, from a client on another subnet: discovery is
answered with the console's normal status, and a TCP 9295 session request gets the same
answer as when sent directly. Not yet verified on a console: pairing, a full stream and
whether the console minds the relay's address in the `Host` header of authenticated requests.

## Testing approach

- Protocol code is tested with known-answer vectors produced by a reference implementation
  (inputs chosen by us, only the outputs are kept) and with in-process fake consoles that
  play the console side of each exchange over loopback sockets.
- Media tests decode small H.264/HEVC clips generated with FFmpeg (`tests/Cord.Media.Tests/Fixtures`).
- Relay tests forward real datagrams and TCP connections between loopback sockets that play
  the client and the console.
- App tests check DualSense effects against byte vectors, capture SDL effect submission and
  resets with a virtual gamepad, and exercise native SDL resampling to verify channel
  routing, sample counts, continuity and the haptics queue's latency bound without hardware.
- Launcher logic (wake-and-play timing, console status, setup gating) has unit tests, and
  every page is drawn for a few frames with a headless Dear ImGui context to catch layout and
  id-stack errors.
- The app has a smoke-test mode: `CORD_SCREENSHOT=<file.bmp>` renders 90 frames, saves a
  screenshot and exits. Combined with `CORD_DATA_DIR` pointing at an empty directory it
  captures the first-run setup.

## Configuration and storage

Per-user application data directory (`%APPDATA%/Cord` on Windows,
`$XDG_CONFIG_HOME/cord` on Linux), or the directory named by the `CORD_DATA_DIR`
environment variable (handy to try the first-run setup without touching real data). JSON files:

- `settings.json`: stream quality (one of the Performance / Balanced / Quality presets or
  custom values), display options and whether the first-run setup was completed.
- `consoles.json`: registered consoles (nickname, MAC, host id, regist key, RP key).
- `account.json`: PSN account id and refresh token. Secrets are stored with the platform
  protection API where available (DPAPI on Windows; on Linux the files are readable only by the user).
