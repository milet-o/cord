# 9. Stream connection messages

The stream connection is Takion version 12 on UDP port 9296. After the Takion handshake,
control messages are protobuf (proto2) `TakionMessage` values carried in DATA chunks with
data type 0. Rumble, pad info and trigger effects use other data types (see
[Input](13-input.md)).

## Sequence

```
Client                                         PS5
  | Takion INIT / INIT_ACK / COOKIE / COOKIE_ACK |
  |---- BIG (launch spec, ECDH pub key) -------->|
  |<--- BANG (ECDH pub key, accepted flags) -----|   both sides enable GKCrypt
  |<--- STREAMINFO (resolutions, audio header) --|
  |---- STREAMINFOACK -------------------------->|
  |---- CONTROLLERCONNECTION ------------------->|
  |<=== video / audio packets ===================|
  |==== feedback state / history ==============>|
  |---- HEARTBEAT every 1 s -------------------->|
  |---- DISCONNECT ----------------------------->|
```

Each "expect" step times out after 5 s. A `DISCONNECT` from the console at any point ends
the session; its `reason` is shown to the user (`"Server shutting down"` means the console
is going to rest mode).

A `STREAMINFO` can arrive before the `BANG` has been processed; keep it and handle it right
after the `BANG`.

## Protobuf schema (subset used by Cord)

Cord keeps its own `takion.proto` containing only what it needs. Field numbers and types
must match the console.

`TakionMessage`:

| # | Field | Type |
|---|---|---|
| 1 | `type` | enum `PayloadType`, required |
| 2 | `big_payload` | `BigPayload` |
| 3 | `bang_payload` | `BangPayload` |
| 6 | `corrupt_payload` | `CorruptFramePayload` |
| 10 | `disconnect_payload` | `DisconnectPayload` |
| 15 | `stream_info_payload` | `StreamInfoPayload` |
| 17 | `connection_quality_payload` | `ConnectionQualityPayload` |
| 22 | `controller_connection_payload` | `ControllerConnectionPayload` |
| 31 | `takion_protocol_request` | `TakionProtocolRequestPayload` (senkusha) |
| 32 | `takion_protocol_request_ack` | `TakionProtocolRequestAckPayload` (senkusha) |
| 14 | `senkusha_payload` | `SenkushaPayload` (senkusha) |

`PayloadType` values used: `BIG = 0`, `BANG = 1`, `HEARTBEAT = 3`, `CORRUPTFRAME = 5`,
`DISCONNECT = 8`, `SENKUSHA = 12`, `STREAMINFO = 13`, `STREAMINFOACK = 14`,
`CONNECTIONQUALITY = 16`, `CONTROLLERCONNECTION = 21`, `IDRREQUEST = 25`,
`TAKIONPROTOCOLREQUEST = 31`, `TAKIONPROTOCOLREQUESTACK = 32`. The enum has more values
(0–32); unknown ones must be ignored.

`BigPayload`: `1 client_version uint32` (required), `2 session_key string` (required),
`3 launch_spec string` (required), `4 encrypted_key bytes` (required),
`5 ecdh_pub_key bytes`, `6 ecdh_sig bytes`.

`BangPayload`: `1 server_version uint32`, `2 token uint32`, `3 encrypted_key_accepted bool`,
`4 version_accepted bool`, `5 session_key string` (all required),
`6 extended_info EventCode`, `7 server_version_string string`, `8 ecdh_pub_key bytes`,
`9 ecdh_sig bytes`.

`StreamInfoPayload`: `1 resolution repeated ResolutionPayload`, `2 audio_header bytes`
(required), `3 start_timeout uint32`, `4 afk_timeout uint32`,
`5 afk_timeout_disconnect uint32`, `6 congestion_control_interval uint32`,
`7 audio_channel repeated AudioChannelPayload`.

`ResolutionPayload`: `1 width uint32`, `2 height uint32`, `3 video_header bytes`
(all required).

`CorruptFramePayload`: `1 start uint32`, `2 end uint32` (required).

`DisconnectPayload`: `1 reason string` (required), `2 extended_info EventCode`.

`EventCode`: `1 event_code string` (required), `2 name string`, `3 description string`.

`ConnectionQualityPayload`: `1 target_bitrate uint32`, `2 upstream_bitrate uint32`,
`3 upstream_loss float`, `4 disable_upstream_audio bool`, `5 rtt double`, `6 loss uint64`.

`ControllerConnectionPayload`: `1 controller_id int32`, `2 connected bool`,
`3 controller_type` enum (`NOTSET 0`, `DUALSHOCK3 1`, `DUALSHOCK4 2`, `VITA 3`,
`XINPUT 4`, `MOBILE 5`, `DUALSENSE 6`, `VR2SENSE 7`).

Senkusha messages are listed in [Senkusha](10-senkusha.md).

## BIG

| Field | Value |
|---|---|
| `client_version` | 12 |
| `session_key` | Session id from the ctrl channel |
| `launch_spec` | Encrypted launch spec, base64 (below) |
| `encrypted_key` | 4 zero bytes |
| `ecdh_pub_key` | Client public key, 65 bytes |
| `ecdh_sig` | HMAC-SHA256(handshake key, public key) |

The encoded `BIG` is larger than one datagram and is fragmented over DATA chunks (see
[Takion](07-takion.md#data-chunks)). Fragments are sized from
`mtu = min(mtu_in, mtu_out) - 50`: the first fragment carries up to `mtu - 26` bytes and
continuation fragments up to `mtu - 25`.

### Launch spec

A JSON object describing the requested stream. It is encrypted by generating the RP cipher
output for a buffer of zeros of the same length (counter 0) and XOR-ing that with the JSON
bytes **including a trailing NUL**, then base64-encoding the result. (The zero buffer is
encrypted, not the JSON; with CFB this distinction matters.)

Known working fields (values that do not depend on settings are fixed strings that the
console accepts as is):

| JSON path | Value |
|---|---|
| `sessionId` | Fixed placeholder string (the real id goes in `BIG.session_key`) |
| `streamResolutions[0].resolution.width` / `.height` | Requested resolution |
| `streamResolutions[0].maxFps` | 30 or 60 |
| `streamResolutions[0].score` | 10 |
| `network.bwKbpsSent` | Requested bitrate in kbps |
| `network.bwLoss` | 0.001 |
| `network.mtu` | `mtu_in` from senkusha |
| `network.rtt` | RTT in ms from senkusha |
| `network.ports` | `[53, 2053]` |
| `slotId` | 1 |
| `appSpecification` | `minFps 30`, `minBandwidth 0`, `extTitleId "ps3"`, `version 1`, `timeLimit 1`, `startTimeout 100`, `afkTimeout 100`, `afkTimeoutDisconnect 100` |
| `konan` | Placeholder `ps3AccessToken` and `ps3RefreshToken` strings |
| `requestGameSpecification` | `model "bravia_tv"`, `platform "android"`, `audioChannels "5.1"`, `language "sp"`, `acceptButton "X"`, `connectedControllers ["xinput","ds3","ds4"]`, `yuvCoefficient "bt601"`, `videoEncoderProfile "hw4.1"`, `audioEncoderProfile "audio1"`, `adaptiveStreamMode "resize"` |
| `userProfile` | Placeholder `onlineId`, `npId`, `region "US"`, `languagesUsed ["en","jp"]` |
| `videoCodec` | `"avc"` (H.264) or `"hevc"` |
| `dynamicRange` | `"SDR"` or `"HDR"` |
| `handshakeKey` | Base64 of the 16-byte handshake key |

The resolution, frame rate and bitrate here are what gives Cord its quality options. Known
working values on PS5: 640×360, 960×540, 1280×720 and 1920×1080, 30/60 fps, with `avc`,
`hevc` and `hevc` + `HDR`. Reasonable defaults are 2, 6, 10 and 15 Mbps for 360p, 540p, 720p
and 1080p. The PS4-only restrictions (no HEVC, no 1080p on base PS4) do not apply.

Verified on a PS5 (standard model, 2026-11-24):

- 1080p was accepted with `bwKbpsSent` up to 100000 (100 Mbps) in both codecs.
- **Resolutions above 1080p are rejected.** 2560×1440 and 3840×2160 complete BIG/BANG, then
  the console sends `DISCONNECT` instead of `STREAMINFO`, whatever the codec or bitrate:
  - `hevc`: `Nagare did not init! AvCap failed to initialize video: [InitResult:-6]`
  - `avc`: `Nagare did not init! AvCap failed to initialize video: [InitResult:-2]`

  "AvCap" is the console's capture/encoder; a client should not offer these resolutions.
  Whether a PS5 Pro accepts them is an open question.

## BANG

The client checks `version_accepted` and `encrypted_key_accepted` (both must be true) and
that `ecdh_pub_key` and `ecdh_sig` are present. It verifies the signature, derives the ECDH
secret and creates the local (index 2) and remote (index 3) GKCrypt instances. From now on
all Takion packets are authenticated, and AV/feedback payloads are encrypted.

## STREAMINFO

- `resolution`: one entry per **adaptive stream** the console may switch between. Each has
  the codec header (`video_header`: H.264 SPS/PPS or HEVC VPS/SPS/PPS in Annex B form) that
  must be fed to the decoder before frames of that stream. Video packets say which entry
  they belong to (`adaptive_stream_index`).
- `audio_header`: 14 bytes, see [Audio](12-audio.md).

Answer with `STREAMINFOACK` (type only), then `CONTROLLERCONNECTION` with
`connected = true` and `controller_type = DUALSENSE` (or `DUALSHOCK4` when the user's pad is
not a DualSense), then start sending input feedback.

## While streaming

| Message | Direction | Content |
|---|---|---|
| `HEARTBEAT` | client → console | Type only, every 1 s |
| `IDRREQUEST` | client → console | Type only. Asks for a new key frame. |
| `CORRUPTFRAME` | client → console | `start`, `end`: frame indices that were lost or broken |
| `CONNECTIONQUALITY` | console → client | Informational |
| `DISCONNECT` | both | `reason` text; the client sends `"Client Disconnecting"` when it quits |

### Detecting a lost connection

Nothing in the protocol tells the client that the console or the network went away: Takion
is UDP, and the ctrl TCP connection may stay open without traffic. A streaming console sends
video, audio and DATA_ACKs continuously (about 55 video frames per second even on a static
screen, observed on a PS5), so the client treats silence as a lost connection. Cord ends
the stream when no datagram at all has arrived for 5 s after STREAMINFO (a 45 s stream on a
real console never came close). It then sends `DISCONNECT` anyway, so a console that is
still reachable stops streaming and accepts the next session request.
