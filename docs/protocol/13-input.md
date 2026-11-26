# 13. Controller input and feedback

Input travels on the stream connection as two unreliable Takion packet types, both
encrypted and authenticated with the local GKCrypt:

- **Feedback state** (type 6): the continuous state (sticks, motion).
- **Feedback history** (type 1): discrete events (buttons, triggers, touchpad).

Console → client feedback (rumble, adaptive triggers, lightbar) arrives as DATA chunks with
special data types.

## Common header (12 bytes)

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Packet type (6 state, 1 history) |
| 1 | 2 | Sequence number (16-bit, separate counter per type) |
| 3 | 1 | Count − 1 (states in the packet, or events in the packet) |
| 4 | 4 | key_pos (low 32 bits) |
| 8 | 4 | GMAC |
| 12 | n | Payload |

Sending: reserve a key position for `n + 16` bytes, encrypt the payload at `key_pos + 16`,
write key_pos, then compute the GMAC over the whole datagram (with the GMAC field zeroed,
key_pos left in place) and write it.

## Feedback state (v12 payload, 28 bytes)

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `0xA0` |
| 0x01 | 2 | Gyro X |
| 0x03 | 2 | Gyro Y |
| 0x05 | 2 | Gyro Z |
| 0x07 | 2 | Accel X |
| 0x09 | 2 | Accel Y |
| 0x0B | 2 | Accel Z |
| 0x0D | 4 | Orientation quaternion, compressed |
| 0x11 | 2 | Left stick X |
| 0x13 | 2 | Left stick Y |
| 0x15 | 2 | Right stick X |
| 0x17 | 2 | Right stick Y |
| 0x19 | 3 | `00 00 01` |

- Gyro and accel are **little-endian** uint16, mapping the ranges −30..30 (gyro, rad/s) and
  −5..5 (accel, g) linearly onto 0..0xFFFF.
- Sticks are **big-endian** int16 (−32768..32767).
- Quaternion compression ("smallest three"), little-endian uint32: find the component with
  the largest absolute value. Bit 0 = 1 if it is negative; bits 1–2 = its index (x, y, z, w
  = 0..3); the other three components, in order, are clamped to ±1/√2, mapped linearly onto
  0..511 and stored in 9-bit fields starting at bits 3, 12 and 21. The identity orientation
  is `w = 1`.

Without motion data, send neutral values (zero gyro, accel `(0, 1, 0)` or zero, identity
quaternion).

Send a state packet when the state changes, but no more often than every 8 ms, and at least
every 200 ms even when nothing changes.

## Feedback history events

Each event is 2, 3 or 5 bytes. A history packet carries several events concatenated,
**newest first**.

### Buttons

Digital buttons are 3 bytes: `80 <code> <value>` with value `FF` pressed, `00` released.

| Button | Code |
|---|---|
| D-pad up / down / left / right | `80` / `81` / `82` / `83` |
| L1 / R1 | `84` / `85` |
| L2 / R2 (analog value 0–255) | `86` / `87` |
| Cross / Circle / Square / Triangle | `88` / `89` / `8A` / `8B` |

Some buttons encode the state in the code and are 2 bytes (`80 <code>`):

| Button | Pressed | Released |
|---|---|---|
| Options | `AC` | `8C` |
| Share (Create) | `AD` | `8D` |
| PS | `AE` | `8E` |
| L3 | `AF` | `8F` |
| R3 | `B0` | `90` |
| Touchpad click | `B1` | `91` |

L2/R2 send an event whenever their analog value changes.

### Touchpad

5 bytes: `D0` (finger down or moved) or `C0` (finger up), then the pointer id (7 bits), then
X and Y as 12-bit values packed into 3 bytes: `x >> 4`, `((x & 0xF) << 4) | (y >> 8)`,
`y & 0xFF`. Up to two fingers. The coordinate range must match the DualSense touchpad
resolution (about 1920 × 1080); confirm the exact maximum against a console.

When recording a state change, emit releases first, then touch changes, then presses, so a
touchpad click is never seen without its finger.

### Reliability

History packets are unreliable, so the sender repeats recent events:

- Keep the last 16 events. Each new event takes the next history sequence number
  (starting at 0).
- Every history packet contains up to the 8 most recent events, and its sequence number is
  the number of its **newest** event. The console remembers the last number it applied and
  only applies as many events as the number moved, so repeated packets are harmless.
- After the newest packet, if nothing new happens, resend the same packet (same sequence
  number) 20, 50, 100 and 200 ms later. Otherwise a lost release would leave a button held
  on the console.

## Console → client feedback

DATA chunks on the stream connection with these data types:

### Rumble (data type 7)

| Offset | Field |
|---|---|
| 0 | Unknown |
| 1 | Left (strong) motor, 0–255 |
| 2 | Right (weak) motor, 0–255 |

### Trigger effects (data type 11), at least 25 bytes

| Offset | Size | Field |
|---|---|---|
| 1 | 1 | Left trigger effect type |
| 2 | 1 | Right trigger effect type |
| 5 | 10 | Left trigger effect parameters |
| 15 | 10 | Right trigger effect parameters |

The type and parameter bytes map directly to the DualSense output report's adaptive trigger
fields (requires `RP-PadProcNo: 3`, see [Ctrl](06-ctrl.md)).

#### Applying effects with SDL3

Pass a 47-byte DualSense effects payload to `SDL_SendGamepadEffect`. This is the effects
body, **without** a USB/Bluetooth report ID, sequence byte or CRC; SDL adds the transport
framing. Start with zeroes, then fill only these fields:

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Enable bits: `0x0C` (`0x04` right trigger, `0x08` left trigger) |
| 10 | 1 | Right trigger effect type |
| 11 | 10 | Right trigger parameters, unchanged from the console |
| 21 | 1 | Left trigger effect type |
| 22 | 10 | Left trigger parameters, unchanged from the console |

The console's left/right order is thus reversed in the SDL effects body. Leave the rumble,
lightbar, audio control and other enable fields zero, so a trigger update changes only the
two triggers. Do not interpret or scale the ten parameter bytes.

Apply effects only to a connected DualSense, on the render thread with the other gamepad
feedback. On stream termination (user disconnect, console disconnect, connection failure
or app exit), discard pending feedback and send the same payload with both effect types
set to **`0x05` (Off)** and all twenty parameters zero. Reset before closing a gamepad as
well, so an active resistance effect cannot survive a session or gamepad switch. Adaptive
triggers work over USB and Bluetooth; the USB-only restriction applies to PCM haptics.

Layout reference: [SDL DualSense effects body](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c).
The Off mode byte is documented by [Nielk1's trigger effect research](https://gist.github.com/Nielk1/6d54cc2c00d2201ccb8c2720ad7538db).

### Pad info (data type 9)

Two layouts, distinguished by size:

| Field | 0x19-byte layout | 0x11-byte layout |
|---|---|---|
| Player index | 8 | 0 |
| Lightbar RGB | 9–11 | 1–3 |
| Motion reset (non-zero) | 12 | 4 |
| Haptic intensity | 20 | 12 |
| Trigger intensity | 21 | 13 |

In the 0x19 layout, bytes 0–1 are the sequence number of the feedback packet being answered
and bytes 4–7 a timestamp.
