# 3. RP crypt

"RP crypt" is the cipher used for registration payloads, the ctrl channel and the launch
spec. It is AES-128 in **CFB-128** mode, with an IV derived from a 64-bit counter.

A cipher instance has two 16-byte values (names inherited from the reverse engineering):

- **bright**: the AES key.
- **ambassador**: input to the IV derivation.

## IV derivation

```
iv = HMAC-SHA256(key = HMAC_KEY_PS5, data = ambassador || counter_be64)[0..16]
```

`counter_be64` is the counter as 8 bytes big-endian. `HMAC_KEY_PS5` is a fixed 16-byte
constant of the PS5 protocol (see [Constant tables](#constant-tables)).

## Encrypt / decrypt

Each message is encrypted independently: create a fresh AES-128-CFB128 transform with key
`bright` and the IV for the given counter, no padding, and process the whole buffer.
Ciphertext length equals plaintext length.

Which counter is used depends on the caller:

| Caller | Counter |
|---|---|
| Registration request and response | always 0 |
| Ctrl channel | one counter per direction, incremented per encrypted item (see [Ctrl](06-ctrl.md)) |
| Launch spec | 0 |

## Registration keys (PS5, PIN based)

For registration, `ambassador` is 16 random bytes chosen by the client and `bright` is
derived from a constant table and the 8-digit PIN:

```
bright[i]     = KEYS_0[i * 0x20 + key_0_off]        for i in 0..15
bright[12..15] ^= PIN as uint32 big-endian
```

`key_0_off` (0..31) is taken from the request payload filler (see
[Registration](04-registration.md)).

The console must be able to recover `ambassador`, so the client also sends an obfuscated
copy called **aeropause**:

```
aeropause[i] = ((ambassador[i] XOR KEYS_1[i * 0x20 + key_1_off]) + 0xD3 + i) mod 256
```

(`0xD3` is `-0x2D` as a byte.)

## Session keys

After the session request returns a 16-byte **nonce**, the cipher for the ctrl channel and
launch spec is derived from the nonce and the stored **RP key** (called "morning"):

```
ka = KEYS_A + (nonce[0] >> 3) * 0x70      // 16 bytes used
kb = KEYS_B + (nonce[7] >> 3) * 0x70      // 16 bytes used

ambassador[i] = ((nonce[i] - 0x2D - i) mod 256) XOR ka[i]
bright[i]     = ((morning[i] + 0x18 + i) mod 256) XOR nonce[i] XOR kb[i]
```

All arithmetic is on bytes (mod 256).

## Constant tables

The protocol depends on fixed tables extracted from the official client:

| Name | Size | Use |
|---|---|---|
| `HMAC_KEY_PS5` | 16 bytes | IV derivation |
| `KEYS_0` | 512 bytes (16 × 32) | Registration `bright` |
| `KEYS_1` | 512 bytes (16 × 32) | Registration `aeropause` |
| `KEYS_A` | 0x70 × 32 bytes | Session `ambassador` |
| `KEYS_B` | 0x70 × 32 bytes | Session `bright` |

The values are not listed in this document. They were taken from open source projects like
Chiaki and are embedded in `Cord.Protocol` as `Crypto/rpcrypt-tables.bin` (8208 bytes: the
HMAC key, `KEYS_0`, `KEYS_1`, `KEYS_A`, `KEYS_B`, in that order). `RpCryptTables.Default`
loads the resource and verifies its SHA-256.

AES-CFB128 is implemented on top of AES-ECB because the .NET CFB API only accepts whole
blocks, while Remote Play messages have arbitrary lengths.

## Test vectors

Known-answer tests must cover: IV derivation for counters 0, 1 and a large value; a
registration payload built from a fixed ambassador, PIN and filler; and session key
derivation from a fixed nonce and morning.
