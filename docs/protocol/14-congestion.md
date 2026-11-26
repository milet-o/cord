# 14. Congestion reports

Every 200 ms the client reports how many AV packets it received and lost since the last
report. The console uses this for rate control.

## Packet (type 5, 15 bytes)

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `0x05` |
| 0x01 | 2 | Unknown, 0 |
| 0x03 | 2 | Packets received |
| 0x05 | 2 | Packets lost |
| 0x07 | 4 | GMAC |
| 0x0B | 4 | key_pos (low 32 bits) |

Reserve a key position for 15 bytes, write key_pos, compute the GMAC with both the GMAC and
key_pos fields zeroed, then restore key_pos and write the GMAC. The packet is not encrypted.

## Counting

Received and lost are derived from packet sequence numbers (gaps count as lost) and reset
after each report.

A client can cap the reported loss ratio at a configurable maximum: if
`lost / (received + lost)` exceeds it, it reports `lost = total × max` and
`received = total − lost`. Reporting the true loss makes the console lower the bitrate
quickly; capping it keeps quality up on noisy networks at the cost of more corrupt frames.
Cord exposes this as an advanced setting.
