# Dependencies and licensing

Cord is released under the MIT license. Every dependency must be compatible with
distributing Cord under MIT; this file records each one and why it is acceptable.

## Relationship with other open source clients

Open source projects like Chiaki are licensed under copyleft licenses (AGPL-3.0). Cord does
**not** contain their code: the protocol was documented in `docs/protocol/` from studying
them, and Cord is implemented from that documentation with its own design. Copying or
translating their source code into Cord would make Cord a derivative work, so it is not
allowed (see `AGENTS.md`).

The protocol constant tables (see `docs/protocol/03-rpcrypt.md`) are data required for
interoperability with the console. They are embedded in `Cord.Protocol` as a binary
resource (`Crypto/rpcrypt-tables.bin`).

The public OAuth client id/secret of the official Remote Play app are built in
(`PsnOAuthOptions.RemotePlayApp`) so PSN sign-in works without configuration.

## Dependencies

| Dependency | Version | Use | License | Notes |
|---|---|---|---|---|
| .NET 10 BCL | 10.0 | Runtime, sockets, crypto | MIT | |
| Google.Protobuf | 3.36.2 | Protobuf runtime | BSD-3-Clause | |
| Grpc.Tools (build only) | 2.85.0 | `.proto` code generation | Apache-2.0 | Not redistributed |
| FFmpeg.AutoGen | 9.0.1.1 | FFmpeg bindings | LGPL-3.0 | Used unmodified as a separate assembly; ship it replaceable with its license |
| FFmpeg shared libraries (native) | 9.0.2 (avcodec-63), BtbN `lgpl-shared` build | Video decoding | LGPL-2.1+ | Downloaded by `build/Ffmpeg.targets` from a pinned BtbN/FFmpeg-Builds release, verified by SHA-256 and copied next to the executable with `ffmpeg-LICENSE.txt`. The libraries stay dynamically linked and replaceable. When updating, change the release, build name and both hashes together; the libavcodec major version must match FFmpeg.AutoGen. |
| Concentus | 2.2.3 | Opus decoding (managed) | BSD-3-Clause | |
| xUnit, Microsoft.NET.Test.Sdk, coverlet | see `Directory.Packages.props` | Tests | Apache-2.0 / MIT | Test only |

| Hexa.NET.SDL3 | 1.2.17 | SDL3 bindings and native SDL3 binaries | MIT (bindings), zlib (SDL3) | |
| Hexa.NET.ImGui | 2.2.9 | Dear ImGui bindings and native cimgui | MIT | |
| Hexa.NET.ImGui.Backends, .Backends.SDL3 | 1.0.18 | ImGui SDL3 platform and SDL_Renderer backends | MIT | |
| System.Security.Cryptography.ProtectedData | 10.0.0 | DPAPI for stored secrets on Windows | MIT | |
| Inter (font) | 4.1, `Inter-Regular.ttf` and `Inter-SemiBold.ttf` from the release archive | UI text | SIL OFL 1.1 | Embedded in `Cord.App` (`Assets/Fonts`), unmodified. The license is shipped next to the executable as `licenses/Inter-LICENSE.txt`, as the OFL requires. |
| Lucide (icon font) | 0.554.0, `lucide.ttf` from the `lucide-static` package | UI icons | ISC (parts from Feather: MIT) | Embedded in `Cord.App` as `Assets/Fonts/Lucide.ttf`; license shipped as `licenses/Lucide-LICENSE.txt`. Codepoints are listed in `Ui/Icons.cs`; take new ones from the same version's `lucide.css`. |

## Using a system FFmpeg

Building with `-p:CordBundleFfmpeg=false` skips the bundled FFmpeg. The FFmpeg folder set in
Settings > Advanced is used when given; otherwise Cord searches `CORD_FFMPEG_PATH`, the
application directory, the usual install locations (winget, scoop, chocolatey, MSYS2,
`/usr/lib`...) and the `PATH`. Only libavcodec 63 (FFmpeg 9) is accepted. Media tests are
skipped when no FFmpeg is found.

## Rules

- Prefer MIT, BSD, Apache-2.0, zlib or MPL-2.0 dependencies. Fonts may also use the SIL Open
  Font License 1.1, which allows bundling with any software as long as the license is shipped.
- LGPL is acceptable only for native libraries linked dynamically and replaceable by the user.
- GPL and AGPL dependencies are not allowed.
- Add a row here in the same change that adds a dependency, and update `THIRD-PARTY-NOTICES.md`
  once the first redistributed dependency is added.
