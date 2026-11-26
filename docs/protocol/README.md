# Sony Remote Play protocol (PS5, local network)

This folder describes the protocol spoken between a Remote Play client and a PlayStation 5
on the same local network. It is written from studying open source projects like Chiaki,
which reverse engineered the official app. It
covers only what Cord needs: PS5, LAN, video, audio and controller input.

Everything here was observed behavior, not an official specification. When a real console
disagrees with this document, the console is right and the document must be fixed.

## Conventions

- All multi-byte integers are **big-endian** unless stated otherwise.
- Offsets and sizes are in hexadecimal when prefixed with `0x`.
- "HTTP-like" means plain text lines separated by `\r\n`, but it is not a compliant HTTP
  implementation. Parse it by hand; do not use an HTTP client library.
- Base64 is standard base64 with padding.

## Connection overview

```
 Client                                                  PS5
   |  1. Discovery (UDP 9302): SRCH / WAKEUP                |
   |------------------------------------------------------->|
   |  2. Registration, once per console (UDP+TCP 9295)      |
   |------------------------------------------------------->|
   |  3. Session request (TCP 9295)  -> nonce               |
   |  4. Ctrl channel (TCP 9295, kept open) -> session id   |
   |  5. Senkusha probe (UDP 9297, Takion v7) -> MTU, RTT   |
   |  6. Stream connection (UDP 9296, Takion v12)           |
   |        BIG -> BANG (ECDH) -> STREAMINFO -> AV + input  |
   |<======================================================>|
```

| Port | Transport | Use |
|---|---|---|
| 9302 | UDP | Discovery and wake-up |
| 9295 | UDP | Registration search (`SRC3` / `RES3`) |
| 9295 | TCP | Registration, session request, ctrl channel |
| 9296 | UDP | Stream connection (Takion) |
| 9297 | UDP | Senkusha network probe (Takion) |
| 9303–9319 | UDP | Local port range the client binds for discovery (falls back to any port) |

The client starts every exchange and the console answers the address and port the request
came from; the console never opens a connection to the client. This is what lets a relay on
the console's network forward the ports unchanged
([console sharing](../architecture.md#console-sharing)).

## Documents

1. [Discovery and wake-up](01-discovery.md)
2. [PSN account id](02-psn-account.md)
3. [RP crypt (registration and session cipher)](03-rpcrypt.md)
4. [Registration](04-registration.md)
5. [Session request](05-session-request.md)
6. [Ctrl channel](06-ctrl.md)
7. [Takion transport](07-takion.md)
8. [Stream encryption: ECDH and GKCrypt](08-stream-crypto.md)
9. [Stream connection messages](09-stream-connection.md)
10. [Senkusha network probe](10-senkusha.md)
11. [Video: AV packets, FEC and frame reassembly](11-video.md)
12. [Audio](12-audio.md)
13. [Controller input and feedback](13-input.md)
14. [Congestion reports](14-congestion.md)

## Persistent data per console

After registration the client stores, per console:

| Name | Size | Source | Used for |
|---|---|---|---|
| Regist key | up to 16 bytes (ASCII hex text, NUL padded) | `PS5-RegistKey` | Session request, ctrl auth, wake-up credential |
| RP key ("morning") | 16 bytes | `RP-Key` | Session cipher key derivation |
| RP key type | integer | `RP-KeyType` | Stored, not used by the stream |
| MAC address | 6 bytes | `PS5-Mac` | Identify the console across IP changes |
| Nickname | text | `PS5-Nickname` | Display |
| Host id | text | discovery `host-id` | Match discovered consoles to registered ones |
