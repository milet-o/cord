# 10. Senkusha network probe

Before the stream connection, the client measures round-trip time and path MTU with a
short-lived, unencrypted Takion connection to UDP port **9297** using Takion **version 7**.
The results go into the launch spec (`network.mtu`, `network.rtt`) and size the `BIG`
fragments.

The socket has "don't fragment" set so that oversized probes are dropped instead of
fragmented.

## Flow

1. Takion handshake (see [Takion](07-takion.md#handshake)).
2. `TAKIONPROTOCOLREQUEST` with `supported_takion_versions = [9]`; wait for
   `TAKIONPROTOCOLREQUESTACK`.
3. `BIG` with `client_version = 9` and empty `session_key`, `launch_spec` and
   `encrypted_key`; wait for `BANG`.
4. RTT test (below).
5. MTU-in and MTU-out tests (below).
6. `DISCONNECT`, close.

Each wait times out after 5 s. If the probe fails, falling back to `mtu = 1454` and
`rtt = 1 ms` is reasonable on a LAN.

## Messages

`SenkushaPayload` (field 14 of `TakionMessage`, type `SENKUSHA = 12`):

| # | Field | Type |
|---|---|---|
| 1 | `command` | enum: `ECHO_COMMAND 0`, `MTU_COMMAND 1`, `BANDWIDTH_COMMAND 3`, `CLIENT_MTU_COMMAND 4` (required) |
| 2 | `echo_command` | `{ 1 state bool }` |
| 3 | `mtu_command` | `{ 1 id uint32, 2 mtu_req uint32, 3 mtu_sent uint32, 4 num uint32, 5 send_delay uint32, 6 delta uint32 }` (`id`, `mtu_req` required) |
| 4 | `bandwidth_command` | not used |
| 5 | `client_mtu_command` | `{ 1 id uint32, 2 mtu_req uint32, 3 state bool, 4 mtu_down uint32 }` (first three required) |

`TakionProtocolRequestPayload` (field 31): `1 supported_takion_versions repeated uint32`.
`TakionProtocolRequestAckPayload` (field 32): `1 takion_protocol_version uint32`.

Senkusha commands are sent as DATA chunks; the client waits for the matching DATA_ACK
before continuing.

## RTT test

1. Send `ECHO_COMMAND { state = true }`.
2. Ten times: send a raw Takion v7 audio-type packet ("ping") with codec `0xFF`,
   `frame_index` = test index, `unit_index` = ping index, units total `0x800`, total
   datagram size `0x224`, zero padded, with a random 32-bit tag at offset 4 of the data
   area. The console echoes it back ("pong"). Match the tag, measure the time. Wait at
   most 1 s per ping.
3. Send `ECHO_COMMAND { state = false }`.
4. RTT = mean of the successful pings. At least one must succeed.

## MTU tests

Search range 576–1454 bytes, 3 retries per step, per-step timeout = `5 × RTT` clamped to
5–500 ms.

- **MTU in** (console → client): binary search. For each candidate size send
  `MTU_COMMAND { id, mtu_req = size, num = 1 }`; if the console's probe packet of that size
  arrives, the size works. The largest working size is `mtu_in`.
- **MTU out** (client → console): binary search where the client sends padded v7 AV
  packets of the candidate size and reports with `CLIENT_MTU_COMMAND`
  (`mtu_req`, `mtu_down = mtu_in`); the console answers with its own
  `CLIENT_MTU_COMMAND` confirming whether it received the probe. The largest working size
  is `mtu_out`.

Size accounting: an IP packet is 28 bytes of IPv4 + UDP headers plus the datagram; the v7
AV header is 0x12 bytes.

The exact reply semantics of the MTU commands must be confirmed against a real console.
Cord currently implements only the RTT test and reports `mtu = 1454` for both directions;
implement the MTU tests once they can be observed on a console, and update this section.

## Takion v7 AV header

Used only by senkusha probes. Same field order as v12 (see [Video](11-video.md)) but the
fixed part is 0x12 bytes, plus 3 bytes for video packets and 3 more when the NALU-info flag
is set.
