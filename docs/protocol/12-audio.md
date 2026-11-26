# 12. Audio

## Audio header

`STREAMINFO.audio_header` is 14 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Channels |
| 1 | 1 | Bits per sample |
| 2 | 4 | Sample rate (Hz) |
| 6 | 4 | Frame size (samples per channel per frame) |
| 10 | 4 | Unknown (1) |

Typical values: 2 channels, 16 bits, 48000 Hz, 480 samples (10 ms). The order of bytes 0
and 1 is not fully confirmed; Cord only reads the header and should sanity check the result
(channels 1–8, bits 16).

## Audio packets

Audio packets use the AV header from [Video](11-video.md#av-packet-header-takion-v12). Only
codec `5` (Opus) is handled. The payload is `total units × unit size` bytes; each unit is one
encoded Opus frame of exactly `unit size` bytes.

Audio uses simple redundancy instead of Reed–Solomon. With `s` source units and `f` FEC
units in a packet whose frame index is `F`:

- source unit `i` (0 ≤ i < s) is audio frame `F + i`;
- FEC unit `j` (0 ≤ j < f) is a repeated copy of audio frame `F − f + j`.

The receiver decodes each audio frame index once, in order, using the copies to fill frames
whose original packet was lost. At the very start of the stream (frame index small), FEC
units that would refer to negative indices are ignored.

Packets with the haptics flag (`0x02` in the audio header's haptics byte) carry the DualSense
haptics stream instead of game audio. They use the same AV unit framing and redundancy,
but their payload is raw PCM, not Opus, even though the AV codec field is `5`. Frame indices
and duplicate suppression are independent for game audio and haptics.

## DualSense haptics

Each haptics unit contains signed 16-bit **little-endian**, interleaved stereo PCM at
**3000 Hz**, independent of `STREAMINFO.audio_header`. Each sample frame is four bytes:
left actuator, then right actuator. Reject units whose size is not a multiple of four.
Never pass these bytes to the Opus decoder.

The DualSense exposes a USB playback device with four channels at **48000 Hz**. Resample
the haptics by a factor of 16 and route the left/right actuator signals to channels **3/4**
(zero-based indices 2/3). Channels 1/2 must be silent: they are audio, not haptic actuators.
Do not use ordinary stereo-to-surround mixing, which can place signals on the wrong channels.

Cord expands the source to `[0, 0, left, right]` at 3000 Hz and uses an SDL audio stream
to resample all four channels to 48000 Hz. The stream retains the resampler's history across
packets; it is not flushed after each unit. As with game audio, queued input beyond 80 ms
is cleared to bound latency. A pooled buffer holds the expanded input.

Only enable this output when the selected gamepad is a **wired DualSense**. Enumerate
playback devices by name (`DualSense`, or the Windows name `Wireless Controller`), request
four channels at 48000 Hz and verify the opened device actually has that channel count and
sample rate. Device names alone do not prove the selected gamepad uses USB. Missing,
unavailable or incompatible devices simply disable haptic playback; never route it to the
default speakers. Bluetooth has no USB audio endpoint and cannot use this path.

Close the haptic output and discard its queue when streaming stops or the gamepad is
disconnected. Re-enumerate on SDL audio device events so an endpoint that appears after the
gamepad can be opened. While USB haptics are available, suppress legacy rumble: SDL's
DualSense rumble emulation selects a mode that disables audio haptics. Other gamepads and
Bluetooth DualSense controllers retain ordinary rumble.

SDL references: [audio streams](https://wiki.libsdl.org/SDL3/SDL_CreateAudioStream),
[device format negotiation](https://wiki.libsdl.org/SDL3/SDL_OpenAudioDevice), and the
[DualSense backend's rumble/audio mode](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c).

## Playback

Decode with Opus at the header's sample rate and channel count, then push PCM to the audio
output with a small jitter buffer. Dropping or stretching samples to keep latency bounded is
an implementation choice documented in `docs/architecture.md`.
