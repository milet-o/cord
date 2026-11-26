# 4. Registration

Registration pairs the client with a console once. The user opens
*Settings → System → Remote Play → Pair Device* on the PS5, which shows an 8-digit PIN.
The client needs: console address, PSN account id (8 bytes) and the PIN.

## Step 1: UDP search

1. Create a UDP socket with broadcast enabled, bound to any local port.
2. Send the 5 bytes `SRC3\0` to UDP port 9295 of the console **and to the broadcast
   address** (the directed broadcast of the console's subnet, plus `255.255.255.255`).
3. Wait up to 3 s for a datagram that starts with `RES3`. Its sender is the console.
   An ICMP "port unreachable" (connection refused) means Remote Play is not listening.
4. Wait about 100 ms before the next step; the console does not accept the TCP request
   immediately.

Observed on a real PS5 (system software of November 2026):

- With the *Pair Device* screen open, the console ignores `SRC3` sent to its own address and
  answers only the broadcast one, from port 9295, with `RES3 01 01 00`.
- It answered the first broadcast `SRC3` only; later ones in the same pairing session got no
  answer, so the TCP request should follow the first `RES3` promptly.
- With the pairing screen closed it answers nothing, so a timeout usually means the user has
  not opened *Pair Device*.

## Step 2: build the payload

The payload is binary, at least `0x1E0` bytes:

| Offset | Size | Content |
|---|---|---|
| 0x000 | 0x1E0 | Filler. Any bytes; `'A'` (0x41) everywhere is known to work. Two filler bytes select table offsets and two ranges are overwritten (below). |
| 0x0C7 | 8 | `aeropause[8..16]` |
| 0x191 | 8 | `aeropause[0..8]` |
| 0x1E0 | n | Encrypted inner header block |

Table offsets come from the filler **before** aeropause is written:

```
key_0_off = payload[0x18D] & 0x1F
key_1_off = payload[0x000] >> 3
```

Then:

1. `ambassador` = 16 random bytes.
2. Build the registration cipher (`bright` from `KEYS_0`, `key_0_off` and the PIN) and
   `aeropause` (from `KEYS_1` and `key_1_off`) as described in [RP crypt](03-rpcrypt.md).
3. Write `aeropause` into the two ranges.
4. The inner header block is the text

   ```
   Client-Type: dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f\r\n
   Np-AccountId: <base64 of the 8-byte account id>\r\n
   ```

   encrypted with the registration cipher at counter 0, appended at offset `0x1E0`.

The payload length is `0x1E0 + length of the inner block`.

## Step 3: HTTP request

Open TCP to the address that answered `RES3`, port 9295, and send:

```
POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n HTTP/1.1\r\n
HOST: 10.0.2.15\r\n
User-Agent: remoteplay Windows\r\n
Connection: close\r\n
Content-Length: <payload length>\r\n
RP-Version: 1.0\r\n
\r\n
<payload>
```

Quirks that must be kept as is:

- The request line repeats ` HTTP/1.1` after a `\r\n`. This mirrors the official client.
- `HOST` is not checked; the fixed value `10.0.2.15` works.

## Step 4: response

Read the header block until `\r\n\r\n` (timeout 3 s).

- Status `200`: read exactly `Content-Length` bytes of body, decrypt them with the same
  registration cipher at counter 0. The plaintext is a list of `Key: Value\r\n` lines.
- Any other status: the `RP-Application-Reason` header holds an error code in hex (see
  table below).

| Key | Format | Meaning |
|---|---|---|
| `PS5-RegistKey` | hex | Hex-decode it; the result is ASCII text (hex digits) of at most 16 bytes. Store NUL padded to 16 bytes. Required. |
| `RP-Key` | hex, 16 bytes | RP key ("morning"). Required. |
| `RP-KeyType` | decimal | Key type. Stored. |
| `PS5-Mac` | hex, 6 bytes | Console MAC address. Required. |
| `PS5-Nickname` | text | Console name. |
| `AP-Ssid`, `AP-Bssid`, `AP-Key`, `AP-Name` | text | Access point info. Ignored by Cord. |
| `RP-SupportCmd` | integer | Ignored. |

Registration succeeds only if `PS5-RegistKey`, `RP-Key` and `PS5-Mac` are all present and
valid.

## Error codes (`RP-Application-Reason`)

These codes are shared with the session request.

| Code | Meaning |
|---|---|
| `0x80108b09` | Registration failed, usually a wrong PIN |
| `0x80108b02` | Invalid PSN account id |
| `0x80108b10` | Remote Play already in use. Also returned to a session request made a few seconds after the previous session ended, while the console is still tearing it down (observed on a PS5; waiting about 15 s was enough). |
| `0x80108b15` | Remote Play crashed on the console |
| `0x80108b11` | RP-Version mismatch |
| `0x80108bff` | Unknown |
