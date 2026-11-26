# 7. Takion transport

Takion is Sony's UDP transport for Remote Play. Its reliable message part is closely
modelled on SCTP (RFC 4960): a four-way handshake with a cookie, tagged associations,
TSN-like sequence numbers and SACK-like acknowledgements. Audio, video, input and congestion
reports travel in their own unreliable packet types.

Two Takion connections are used per session:

| Use | Port | Takion version | Encryption |
|---|---|---|---|
| Senkusha probe | 9297 | 7 | none |
| Stream connection | 9296 | 12 (PS5) | GKCrypt after `BANG` |

The client connects its UDP socket to the console port. Set "don't fragment" on the socket
(`IP_DONTFRAGMENT` on Windows, `IP_MTU_DISCOVER = IP_PMTUDISC_DO` on Linux) so MTU probing is
meaningful; for the stream connection it can be made configurable.

## Packet types

The low nibble of the first byte of every datagram is the packet type:

| Value | Type | Direction |
|---|---|---|
| 0 | Control (carries a message/chunk) | both |
| 1 | Feedback history (button events) | client → console |
| 2 | Video | console → client |
| 3 | Audio (and haptics) | console → client |
| 4 | Handshake | not used by the client |
| 5 | Congestion report | client → console |
| 6 | Feedback state (sticks, motion) | client → console |
| 8 | Client info | not used by the client |

For video and audio, bit 4 of the first byte is a flag ("uses NALU info structs", see
[Video](11-video.md)).

## Control packets

```
offset  size  field
0x00    1     packet type = 0
0x01    4     tag: the receiver's association tag (0 in INIT)
0x05    4     GMAC (zero before authentication)
0x09    4     key_pos (low 32 bits)
0x0D    1     chunk type
0x0E    1     chunk flags
0x0F    2     chunk length = payload length + 4
0x11    n     chunk payload
```

The 16 bytes from offset 1 are the "message header". A received chunk is valid only if the
datagram length equals `chunk length + 13`.

### Chunk types

| Value | Chunk |
|---|---|
| 0x00 | DATA |
| 0x01 | INIT |
| 0x02 | INIT_ACK |
| 0x03 | DATA_ACK |
| 0x0A | COOKIE |
| 0x0B | COOKIE_ACK |

## Handshake

1. **INIT** (client → console), tag 0, key_pos 0, flags 0, 16-byte payload:

   | Offset | Size | Field | Client value |
   |---|---|---|---|
   | 0 | 4 | Initiate tag | random 32-bit `tag_local` |
   | 4 | 4 | a_rwnd | `0x19000` |
   | 8 | 2 | Outbound streams | `0x64` |
   | 10 | 2 | Inbound streams | `0x64` |
   | 12 | 4 | Initial sequence number | `tag_local` |

2. **INIT_ACK** (console → client), flags 0, 48-byte payload: the same 16-byte layout (with
   the console's tag) followed by a 32-byte cookie. Reject it when the tag is 0, either
   stream count is 0, outbound streams > `0x64` or inbound streams < `0x64`. The console's
   tag becomes `tag_remote` and is written in the tag field of every later control packet.
3. **COOKIE** (client → console): echo the 32-byte cookie as payload.
4. **COOKIE_ACK** (console → client), flags 0, empty.

Each step is retried up to 3 times. While waiting for COOKIE_ACK, a repeated INIT_ACK can
arrive and must be skipped. Existing clients use the console's tag as the initial remote
sequence number rather than the value in the INIT_ACK payload.

After the handshake the connection is up. Packets that arrive while the stream cipher is not
yet configured (between handshake and `BANG`) are kept in a small queue (32 entries) and
processed once the cipher exists.

## DATA chunks

Payload of a DATA chunk:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Sequence number (TSN), increments by 1 per DATA chunk sent |
| 4 | 2 | Channel |
| 6 | 2 | Zero |
| 8 | 1 | Data type (first fragment only) |
| 9 | n | Data |

Data types:

| Value | Type |
|---|---|
| 0 | Protobuf `TakionMessage` (see [Stream connection](09-stream-connection.md)) |
| 7 | Rumble |
| 9 | Pad info (DualSense LED, player index, motion reset, intensities) |
| 11 | Trigger effects |

Chunk flags carry fragmentation: a message larger than the MTU is split into several DATA
chunks, each with its own sequence number. The first fragment carries the data type byte;
continuation fragments omit it (8-byte header). The last fragment has flags `1`, earlier
ones flags `0`. A message that fits in one chunk is sent with flags `1`.

The client sends its protobuf messages on channel 1.

Received DATA chunks go through a reorder queue (16 entries) keyed by sequence number and
are delivered in order. After delivering, the client acknowledges the highest delivered
sequence number.

## DATA_ACK chunks

12-byte payload, flags 0:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Cumulative sequence number acknowledged |
| 4 | 4 | a_rwnd (`0x19000`) |
| 8 | 2 | Gap ack block count (client sends 0) |
| 10 | 2 | Duplicate TSN count (0) |

Gap ack blocks (4 bytes each) follow when the count is non-zero. The client keeps sent DATA
chunks in a send buffer and retransmits them until they are acknowledged.

Cord's choices where the protocol leaves room: unacknowledged chunks are resent every
200 ms, at most 10 times. Received chunks are treated as complete messages (the console has
not been seen fragmenting messages to the client). If a gap in received sequence numbers is
not filled within 500 ms, or a chunk arrives too far ahead of the expected number, delivery
skips ahead instead of stalling.

## Authentication of control packets

Once the stream cipher is set, every control packet carries a 4-byte GMAC and a key_pos:

- **Sending**: reserve a key position (see below), write its low 32 bits at offset 9, zero
  the GMAC field and the key_pos field, compute the GMAC over the whole datagram with that
  key position, then restore key_pos and write the GMAC at offset 5.
- **Receiving**: compute the same thing with the console's cipher and compare. Packets with
  a wrong GMAC are dropped.

Control payloads are authenticated but **not encrypted**.

### Key positions

Each side keeps a 64-bit key position counter for its own cipher, starting at 0. Sending a
packet reserves a range: the packet uses the current value, and the counter advances by
`n + (n mod 16)` where `n` is the size passed by the sender (this is an observed quirk, not
real rounding). Sizes that the console accepts:

| Packet | n |
|---|---|
| DATA chunk | data length (without headers) |
| DATA_ACK | whole datagram length |
| Congestion report | 15 (packet length) |
| Feedback state / history | payload length + 16 |

When no cipher is set, key_pos is always 0.

Only the low 32 bits travel on the wire. The receiver reconstructs the full 64-bit value
from the last accepted position: if the new low value is "after" the previous one in
32-bit serial arithmetic but numerically smaller, the high part wrapped forward; if it is
"before" and numerically larger, it wrapped backward.

## Other packet types

Layouts of video/audio, feedback and congestion packets are in their own documents. Their
GMAC and key_pos offsets:

| Type | GMAC offset | key_pos offset |
|---|---|---|
| Control | 0x05 | 0x09 |
| Video, audio | 0x0A | 0x0E |
| Congestion | 0x07 | 0x0B |
| Feedback state/history | 0x08 | 0x04 |

For control and congestion packets the key_pos field is zeroed while computing the GMAC; for
other types it is left in place.
