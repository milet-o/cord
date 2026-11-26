# 1. Discovery and wake-up

Discovery is a tiny text protocol over UDP. The client sends a request to the console's
discovery port (9302 for PS5), either unicast or to the broadcast address, and the console
answers from that port with an HTTP-like status line and headers.

## Client socket

Bind a UDP socket on the first free port in 9303–9319, falling back to an OS-assigned port.
Enable `SO_BROADCAST`. The same socket is used to send and receive.

Datagrams are text with `\n` line endings, and the client sends them **including a
terminating NUL byte**.

## Search (`SRCH`)

```
SRCH * HTTP/1.1\n
device-discovery-protocol-version:00030010\n
```

`00030010` is the PS5 discovery protocol version (PS4 uses `00020020` on port 987; Cord
ignores PS4).

Send periodically (for example once per second) to the broadcast address of each interface
and/or to known console addresses.

Observed on Windows: a `SRCH` sent only to the limited broadcast address `255.255.255.255`
may leave through a single interface and never reach the console. Cord also sends it to the
directed broadcast address of every IPv4 interface (address OR NOT mask) and to the last
known address of each registered console; unicast `SRCH` is answered normally.

## Search response

The console replies with a status line and `key:value` headers:

```
HTTP/1.1 200 Ok
host-id:<hex id>
host-type:PS5
host-name:<nickname>
host-request-port:<port>
device-discovery-protocol-version:00030010
system-version:<decimal version>
running-app-titleid:<title id, optional>
running-app-name:<title name, optional>
```

| Status code | Meaning |
|---|---|
| 200 | Console is on and ready |
| 620 | Console is in rest mode (standby) |
| other | Unknown state |

The sender address of the datagram is the console address. A response is a PS5 when
`device-discovery-protocol-version` is `00030010`. `system-version` ≥ `8050001` means the
console speaks Remote Play version `1.0` (the only version Cord targets).

Header names are matched exactly; unknown headers are ignored.

## Wake-up (`WAKEUP`)

Sent to UDP port 9302 of a console in rest mode:

```
WAKEUP * HTTP/1.1\n
client-type:vr\n
auth-type:R\n
model:w\n
app-type:r\n
user-credential:<decimal uint64>\n
device-discovery-protocol-version:00030010\n
```

`user-credential` is derived from the stored regist key: take the regist key bytes up to the
first NUL, interpret them as an ASCII hexadecimal string (at most 8 characters), parse that
as an unsigned integer in base 16 and print it in **decimal**.

Example: regist key text `1a2b3c4d` → credential `439041101`.

There is no response to a wake-up. Poll with `SRCH` until the console answers `200`.
