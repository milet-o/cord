# 11. Video: AV packets, FEC and frame reassembly

## AV packet header (Takion v12)

Video (type 2) and audio (type 3) packets share a header. Offsets are from the start of the
datagram.

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | Low nibble: type (2 video, 3 audio). Bit 4: NALU-info flag. |
| 0x01 | 2 | Packet index (16-bit sequence number, per type) |
| 0x03 | 2 | Frame index (16-bit sequence number) |
| 0x05 | 4 | Unit word (below) |
| 0x09 | 1 | Codec |
| 0x0A | 4 | GMAC |
| 0x0E | 4 | key_pos (low 32 bits) |

Video only:

| Offset | Size | Field |
|---|---|---|
| 0x12 | 2 | Unknown |
| 0x14 | 1 | Bits 5–7: adaptive stream index (which `STREAMINFO` resolution) |
| 0x15 | 3 | Present only when the NALU-info flag is set; skipped |

The video payload starts at 0x15 (or 0x18 with the NALU-info flag).

Audio only (v12):

| Offset | Size | Field |
|---|---|---|
| 0x12 | 1 | Unknown |
| 0x13 | 1 | `0x02` = this packet carries DualSense haptics instead of game audio |

The audio payload starts at 0x14. With the NALU-info flag, 3 extra bytes sit between 0x13
and the haptics byte, moving it to 0x16 and the payload to 0x17.

These offsets come from studying existing open source clients and must be confirmed with a
capture.

### Unit word

Video:

| Bits | Field |
|---|---|
| 21–31 (11 bits) | Unit index within the frame |
| 10–20 (11 bits) | Total units in frame − 1 (source + FEC) |
| 0–9 (10 bits) | FEC units in frame |

Audio:

| Bits | Field |
|---|---|
| 24–31 | Unit index |
| 16–23 | Total units − 1 |
| 8–15 | Unit size in bytes |
| 4–7 | FEC unit count |
| 0–3 | Source unit count |

Received AV packets go through a reorder queue keyed by packet index (64 entries for
video). A gap is waited for at most ~16 ms (one frame at 60 fps) before it is skipped.

## Decryption

Verify the GMAC (see [Takion](07-takion.md#authentication-of-control-packets)), then
decrypt the payload with the remote GKCrypt at `key_pos + 16` (see
[Stream crypto](08-stream-crypto.md)).

## Frame structure

A video frame is split into `k` **source units** followed by `m` **FEC units**
(`k = total − fec`, `m = fec`, at least one FEC unit is assumed). All units of a frame have
the same nominal size `S`:

- Every source unit starts with a 2-byte big-endian **padding count** `p`. The unit was
  shortened by `p` bytes for transmission, so `S = received length + p`. The first source
  unit received fixes `S` for the frame.
- FEC units are always sent at full size `S`.
- To reassemble, place each unit in a zero-filled slot of `S` bytes (round the slot stride
  up to a multiple of 16 for alignment).

When all `k` source units arrived, the frame is the concatenation, in unit order, of each
source unit **without its 2-byte padding count** (only its received bytes).

### FEC recovery

If some source units are missing but the number of received units (source + FEC) is at
least `k`, the missing ones are recovered with **Cauchy Reed–Solomon** erasure coding over
GF(2^8):

- Word size 8, field polynomial `x^8 + x^4 + x^3 + x^2 + 1` (0x11D), as in the Jerasure
  library's defaults.
- Coding matrix (`m` rows × `k` columns), Jerasure's "original Cauchy" matrix:
  `M[i][j] = 1 / (i XOR (m + j))` in GF(2^8).
- Coding unit `i` = XOR over `j` of `M[i][j] · source_j` (byte-wise GF multiply), computed
  over the full `S`-byte slots (padding count included, zero filled).

After recovery, a recovered unit's length is `S − p`, with `p` read from its first 2 bytes.

Test with round-trip encode/erase/decode tests and, ideally, a captured frame.

## Frame sequencing

- Frames arrive in increasing frame index order. Packets for a frame older than the current
  one are dropped.
- When a packet for a newer frame arrives, the current frame is finished (flushed) first.
- A frame is also flushed as soon as enough units arrived, or the last unit index arrived.
- If frame indices were skipped (`new > last complete + 1`), send `CORRUPTFRAME` with
  `start = last complete + 1` and `end = new − 1`.
- If FEC fails, send `CORRUPTFRAME` for the broken range and, optionally, `IDRREQUEST`.
  While waiting for the IDR, drop non-I frames and repeat the request every second.

When a packet's adaptive stream index differs from the current one, send that stream's
`video_header` (from `STREAMINFO`) to the decoder before the next frame; the resolution may
have changed.

## Decoder input

Frames are Annex B H.264 or HEVC access units. Feed them to the decoder as is, prefixed by
the stream header when it changes. A client can also rewrite the reference frame index in
P-slice headers when the referenced frame was lost, to keep decoding without waiting for an
IDR; Cord treats this as a later optimization.
