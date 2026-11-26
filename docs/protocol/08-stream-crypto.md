# 8. Stream encryption: ECDH and GKCrypt

The stream connection (Takion on port 9296) is protected by a per-session key agreed with
ECDH during the `BIG`/`BANG` exchange, and a custom AES-based scheme known as
**GKCrypt**: an AES-CTR-like keystream for confidentiality and a truncated GMAC for
integrity.

## ECDH

- Curve: **secp256k1**.
- The client generates a fresh key pair per session.
- Public keys are exchanged as uncompressed SEC1 points (65 bytes, `04 || X || Y`).
- Each public key is signed with `HMAC-SHA256(key = handshake key, data = public key)`
  (32 bytes). The client sends its signature in `BIG` and should verify the console's
  signature from `BANG` the same way.
- Shared secret: the 32-byte X coordinate of the ECDH result (raw secret agreement, no KDF).

.NET note: `ECDiffieHellman` with `ECCurve.CreateFromValue("1.3.132.0.10")` (secp256k1) and
`DeriveRawSecretAgreement` works on Windows (CNG). Linux uses OpenSSL, which also supports the
curve; if a platform ever refuses it, switch to explicit curve parameters or BouncyCastle.

## GKCrypt instances

Two independent instances are derived from the same secret:

| Instance | Index | Used for |
|---|---|---|
| Local | 2 | Packets the client sends |
| Remote | 3 | Packets the console sends |

### Key and IV derivation

```
data      = 01 || index || 00 || handshake_key (16 bytes) || 01 00
h         = HMAC-SHA256(key = ecdh_secret, data)        // 32 bytes
key_base  = h[0..16]
iv        = h[16..32]
```

### Counter arithmetic

"`iv + n`" below means: treat the 16-byte IV as a **little-endian** 128-bit integer, add
`n`, and keep 16 bytes. (Carry propagates from byte 0 upwards.)

### Keystream

The keystream is AES-128-ECB with key `key_base` applied to consecutive counter blocks:

```
keystream block j = AES-ECB(key_base, iv + j)
```

The byte at absolute position `p` of the keystream is byte `p mod 16` of block `p / 16`.
To encrypt or decrypt `n` bytes at key position `p`, XOR them with keystream bytes
`p .. p + n - 1`. Implementations should pre-generate keystream ahead of the current
position (for example a 256 KiB ring buffer filled by a background thread) because AV
decryption is on the hot path.

Which key position is used for the bytes of a packet:

| Data | Key position |
|---|---|
| Video/audio payload (console → client) | packet key_pos + 16 |
| Feedback state/history payload (client → console) | packet key_pos + 16 |
| Control packet payloads | not encrypted |

### GMAC

The GMAC of a packet with key position `p`:

```
gmac_iv  = iv + (p / 16)                 // 16 bytes
tag      = AES-128-GCM(key = gmac_key(p), iv = gmac_iv,
                       plaintext = empty, aad = whole datagram)
gmac     = tag[0..4]
```

The IV is **16 bytes**, so GCM derives its pre-counter block with GHASH (the non-96-bit IV
path of GCM). .NET `AesGcm` only accepts 12-byte nonces, so Cord implements GHASH itself
(`GHash`, 4-bit table multiplication) and builds the GMAC from AES-ECB and GHASH. The GHASH
implementation is tested against the platform `AesGcm` tag for 12-byte nonces.

### GMAC key rotation

The GMAC key changes every 45 000 bytes of key position:

```
key_index = (p == 0) ? 0 : (p - 1) / 45000

fold(x)   = x[0..16] XOR x[16..32]            // 32 -> 16 bytes

gmac_key_base = fold(SHA-256(key_base || (iv + 0)))
gmac_key(0)   = gmac_key_base
gmac_key(k)   = fold(SHA-256(gmac_key_base || (iv + k * 44910)))   for k > 0
```

Note the two different constants (45000 for the index, 44910 for the IV offset); both are
observed values. Packets can arrive slightly out of order, so an implementation caches the
current key and computes older ones on demand rather than discarding them.

## Test vectors

Unit tests must cover: key/IV derivation for indices 2 and 3 from a fixed secret and
handshake key; keystream at aligned and unaligned positions; the 16-byte-IV GMAC against a
reference GCM implementation; GMAC key rotation at positions 0, 1, 45000, 45001 and 90001.
