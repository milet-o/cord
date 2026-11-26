# 5. Session request

The session request asks a registered, awake console for a new streaming session and
returns a **nonce** that keys the session cipher.

## Before connecting

The client generates, per session:

| Value | Size | Content |
|---|---|---|
| Handshake key | 16 bytes | Random. Used in the launch spec and to sign ECDH keys. |
| ECDH key pair | secp256k1 | Fresh key pair (see [Stream crypto](08-stream-crypto.md)). |
| Device id ("did") | 32 bytes | `00 18 00 00 00 07 00 40 00 80`, then 16 random bytes, then six `00` bytes. |

## Request

TCP to `<console>:9295`:

```
GET /sie/ps5/rp/sess/init HTTP/1.1\r\n
Host: <console address>:9295\r\n
User-Agent: remoteplay Windows\r\n
Connection: close\r\n
Content-Length: 0\r\n
RP-Registkey: <regist key>\r\n
Rp-Version: 1.0\r\n
\r\n
```

`RP-Registkey` is the hex encoding of the stored regist key bytes up to the first NUL. Since
those bytes are themselves ASCII hex text, this equals the original `PS5-RegistKey` header
value from registration.

## Response

Read the header block until `\r\n\r\n`.

| Header | Meaning |
|---|---|
| `RP-Nonce` | Base64 of 16 bytes. Present on success. |
| `RP-Version` | Console RP version (matched case-insensitively). |
| `RP-Application-Reason` | Hex error code on failure (see [Registration](04-registration.md#error-codes-rp-application-reason)). |

Success is status `200` with a valid 16-byte nonce. With the nonce and the stored RP key the
client builds the session cipher (see [RP crypt](03-rpcrypt.md#session-keys)).

If the console answers with a version mismatch (`0x80108b11` or `0x80108bff`) and a
different `RP-Version`, the request may be retried once with that version. For PS5 the only
known version is `1.0`, so Cord treats any other value as unsupported.

The TCP connection is closed after the response.
