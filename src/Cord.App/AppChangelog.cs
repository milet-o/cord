namespace Cord.App;

internal sealed record PatchEntry(string Version, string DateTime, string Title, string[] Changes);

/// <summary>Ordered release history displayed in the in-app Patch Notes viewer.</summary>
internal static class AppChangelog
{
    public static IReadOnlyList<PatchEntry> Entries { get; } =
    [
        new(
            "v0.1.9",
            "26/11/2026 17:35",
            "Quick-Start Guide, Live GitHub Patch Notes & Auto-Update",
            [
                "Added a 5-page interactive Quick-Start Guide on first launch (covering PS5 settings, PSN sign-in & pairing, Play/Audio/Web modes, best quality/Frame Gen tuning, and Telegram Bot setup), reopenable anytime from the top bar.",
                "Connected Patch Notes directly to GitHub Releases for live real-time updates alongside built-in history.",
                "Added an automatic new-release notification popup with release highlights and one-click Windows update installation.",
                "Audited and scrubbed all default configuration, binaries, and test fixtures so credentials remain 100% local and per-user.",
            ]),
        new(
            "v0.1.8",
            "26/11/2026 17:05",
            "Adaptive Layout & Audio + Pad Mode (500 Hz)",
            [
                "Made Cord's window and home screen cards adapt to any display/window size (clamped to usable monitor bounds, responsive columns/card heights, wrapping buttons) so bottom controls never clip.",
                "Added one-click Audio + Pad (HDMI companion) mode right beside Play and Web on each console card: zero video decode, 500 Hz / 2 ms controller input even when unfocused or minimized to tray, 40 ms audio buffer, and ~1 Mbps LAN traffic.",
            ]),
        new(
            "v0.1.7",
            "26/11/2026 16:45",
            "Idle Bitrate Fix, Monotonic Frame Gen & Patch Notes",
            [
                "Fixed browser & Web Share bitrate collapsing below 1 Mbps / 12 fps after idle or background tabs (capped synthetic loss at 10%, ignored hidden-tab stats, faster AIMD recovery, and auto IDR keyframe on resume).",
                "Replaced CPU-side 30 fps alternating phase guesses with GPU-authoritative monotonic Frame Gen alpha in WebGL2 to eliminate frame rollbacks.",
                "Disabled Frame Gen automatically when decoded source rate drops below 24 fps.",
                "Added this clickable Patch Notes screen directly inside Cord (top-bar version badge and Settings).",
            ]),
        new(
            "v0.1.6",
            "26/11/2026 15:53",
            "VSync Stutter & Black Flash Fixes",
            [
                "Enabled WebGL2 preserveDrawingBuffer and removed desynchronized canvas mode to eliminate single-frame black flashes.",
                "Moved Web Share MAD & history gating 100% onto GPU shaders with non-blocking PIXEL_PACK_BUFFER fenceSync readback (removed synchronous getImageData stalls).",
                "Aligned Web Share frame consumption to requestAnimationFrame VBlanks when VSync is active.",
                "Upgraded native D3D11 FlipDiscard swapchain to triple-buffering (BufferCount = 3) with MaximumFrameLatency = 1.",
            ]),
        new(
            "v0.1.5",
            "26/11/2026 14:44",
            "Centralized App Versioning",
            [
                "Added single-source pre-1.0 versioning in Directory.Build.props stamped onto desktop binaries and Web Share templates.",
                "Displayed the version badge in the Cord desktop top bar, window title, Settings footer, and Web Share Guest/Admin headers.",
            ]),
        new(
            "v0.1.4",
            "26/11/2026 14:35",
            "VSync Toggle & Continuous Presenter",
            [
                "Added VSync toggle and Frame Generation smoothness warnings in native Settings, F1 stream menu, and Web Share lobby/HUD.",
                "Added continuous VSync-locked rendering and temporal EMA optical flow smoothing across frames.",
            ]),
        new(
            "v0.1.3",
            "26/11/2026 13:06",
            "Frame Gen v2 & Direct Browser Launch",
            [
                "Upgraded D3D11 and WebGL2 Optical Flow to coarse-to-fine ZSAD with forward-backward occlusion confidence.",
                "Added static-scene passthrough detection and alternating 30 fps cadence detection.",
                "Added Open and Open admin browser buttons directly in the Cord desktop Web Share panel.",
            ]),
        new(
            "v0.1.2",
            "26/11/2026 12:38",
            "Browser GPU Boost & Frame Gen",
            [
                "Added WebGL2 GPU Boost (sharpening, debanding, contrast, vibrance) and initial Frame Generation in the Web Share client.",
                "Added 250 Hz browser input polling and live stream statistics HUD.",
            ]),
        new(
            "v0.1.1",
            "26/11/2026 12:24",
            "Low-Latency Native Pipeline",
            [
                "Added native D3D11 Flip Model swapchain with 250 Hz waitable input slicing and AMD FidelityFX CAS sharpening.",
                "Added native D3D11 optical flow Frame Generation (F7) and keyboard/mouse right-stick remapping.",
            ]),
        new(
            "v0.1.0",
            "26/11/2026 11:41",
            "Cord Foundation & Web Share Release",
            [
                "Created the Cord desktop client and launcher with local DPAPI-protected credential storage.",
                "Added PS1 Concrete Light theme alongside Obsidian Dark and full Web Share hosting.",
            ]),
    ];
}
