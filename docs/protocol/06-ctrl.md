# 6. Ctrl channel

The ctrl channel is a long-lived TCP connection to `<console>:9295`. It authenticates the
client, delivers the **session id** needed by the stream connection, and carries
out-of-band commands (heartbeats, login passcode, rest mode, PS button, etc.). It stays open
for the whole session; closing it ends the session.

All encrypted values use the session RP cipher (see [RP crypt](03-rpcrypt.md#session-keys))
with two independent counters, both starting at 0 for each ctrl connection:

- **local counter**: incremented every time the client encrypts something.
- **remote counter**: incremented every time the client decrypts something from the console.

## Ctrl request

Encrypt and base64-encode, in this order (local counters 0–4):

| Counter | Header | Plaintext |
|---|---|---|
| 0 | `RP-Auth` | The 16-byte regist key (NUL padded) |
| 1 | `RP-Did` | The 32-byte device id |
| 2 | `RP-OSType` | `Win10.0.0` followed by one NUL byte (10 bytes) |
| 3 | `RP-StartBitrate` | 4 zero bytes |
| 4 | `RP-StreamingType` | uint32 **little-endian**: `1` H.264, `2` HEVC, `3` HEVC HDR |

Then send:

```
GET /sie/ps5/rp/sess/ctrl HTTP/1.1\r\n
Host: <console address>:9295\r\n
User-Agent: remoteplay Windows\r\n
Connection: keep-alive\r\n
Content-Length: 0\r\n
RP-Auth: <b64>\r\n
RP-Version: 1.0\r\n
RP-Did: <b64>\r\n
RP-ControllerType: 3\r\n
RP-ClientType: 11\r\n
RP-OSType: <b64>\r\n
RP-ConPath: 1\r\n
RP-PadProcNo: 3\r\n
RP-StartBitrate: <b64>\r\n
RP-StreamingType: <b64>\r\n
\r\n
```

`RP-PadProcNo: 3` makes the PS5 send adaptive trigger effects in the format a DualSense
applies directly; without it some effects arrive in an unusable form.

If no response arrives within 5 s, reconnect the TCP socket and send the request once more.

## Ctrl response

Status `200` means success. Headers:

| Header | Meaning |
|---|---|
| `RP-Server-Type` | Base64 of 16 bytes. Decrypt with the **remote** counter (this consumes remote counter 0). Byte 0: `0` PS4, `1` PS4 Pro, `2` PS5. |
| `RP-Prohibit` | `1` means the current content cannot be streamed (the console shows a blocked screen). |

Any bytes received after the header block already belong to the ctrl message stream.

## Message framing

Both directions use the same frame:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Payload size |
| 4 | 2 | Message type |
| 6 | 2 | Zero |
| 8 | n | Payload, RP-encrypted |

The payload is encrypted with the sender's next counter **only when it is non-empty**;
empty messages do not consume a counter. The receiver decrypts each non-empty payload
with its next remote counter.

## Message types

| Type | Direction | Name | Payload |
|---|---|---|---|
| `0x0033` | console → client | Session id | Byte `0x4A`, then the session id as ASCII (`[A-Za-z0-9]`, 24–78 chars) |
| `0x00FE` | console → client | Heartbeat request | empty |
| `0x01FE` | client → console | Heartbeat reply | empty |
| `0x0004` | console → client | Login passcode request | empty |
| `0x8004` | client → console | Login passcode reply | Passcode digits as ASCII |
| `0x0005` | console → client | Login result | 1 byte: `0` success, `1` wrong passcode |
| `0x0034` | console → client | Switch to stream connection | empty (used with PSN connections) |
| `0x0050` | client → console | Go to rest mode | empty |
| `0x0014` | client → console | Go home (PS button screen) | 16 bytes: `00 FF` followed by 14 zeros |
| `0x0013` | client → console | Enable DualSense features | `00 40 00` |
| `0x0011` | client → console | Sent together with `0x13` | 16 bytes, see below |
| `0x0910` | client → console | Display devices | 4 zero bytes |
| `0x0001` | console → client | Display state A | byte 0: `1` cannot display, `0` can display |
| `0x0016` | console → client | Display state B | `01 FF` means cannot display |
| `0x0030` | client → console | Microphone connect | `00 00` |
| `0x0036` | client → console | Microphone mute toggle | 4 bytes |
| `0x000D`, `0x0020`–`0x0025` | both | On-screen keyboard | Out of scope for Cord |

The known working `0x0011` payload is
`A0 AB 51 BD D1 7E 00 00 FF FF FF FF FF 00 00 00`; its meaning is unknown.

## Session flow on the ctrl channel

1. After the ctrl response, wait up to 10 s for either a **session id** or a **login
   passcode request**.
2. Passcode request: ask the user for the console login passcode, send `0x8004`, then wait
   again. A login result of `1` means the passcode was wrong; ask again.
3. On the session id, send the feature messages (`0x13`, `0x11` when a DualSense is used,
   and `0x910`). The session id is required by the stream connection (`BIG` message).
4. Answer every heartbeat request with a heartbeat reply. Losing heartbeats makes the
   console drop the session.

If the console never sends a session id, the client can fabricate one (current time in seconds
followed by base64 of 48 random bytes) and continues; the console accepts it. Cord may do
the same, logging a warning.
