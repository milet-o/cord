# AGENTS.md

Guidance for anyone (human or AI agent) working on Cord.

## What Cord is

Cord is a cross-platform (Windows and Linux first) PlayStation 5 Remote Play client written in
C# / .NET. The scope is deliberately small: sign in, add a PS5, play. It should feel like the
official Remote Play app, but with more control over resolution, frame rate, codec and bitrate.

Out of scope unless explicitly requested: PS4 support and remote play over the internet through
Sony's servers. Playing from another network is covered only by console sharing (a relay on the
console's network, see `docs/architecture.md`).
Do not add features beyond this scope without asking.

## The protocol documentation is the source of truth

- `docs/protocol/` documents the Sony Remote Play protocol. Code that speaks the protocol is
  implemented **from these documents**.
- Open source projects like Chiaki are used only to understand how the protocol works. Do not
  copy or port their code line by line. When something is unclear, study the reference, update the docs first, then
  implement.
- Any protocol discovery, correction or open question found while coding **must** be written
  back to `docs/` in the same change. Docs that disagree with the code are a bug.
- Architectural decisions go in `docs/architecture.md`. New third-party dependencies go in
  `docs/dependencies.md`, with their license.

## Definition of done

A task is only done when all of the following are true:

1. The code builds with no warnings (`dotnet build` with warnings treated as errors).
2. Tests exist for the new behavior and pass (`dotnet test`). Protocol code (parsers, crypto,
   packet formats, FEC) must have unit tests with known vectors or round-trip tests.
3. The change was exercised for real when it touches runtime behavior (for example, the app
   was run against a console, or a tool was used to check the output), or the report clearly
   says that this could not be done and why.
4. `docs/` reflects the change.

Never report a task as complete if a test fails or a step was skipped. Say what failed.

## Code guidelines

- Clean code: small focused types, clear names, no dead code, no speculative abstractions.
- Strict separation between projects (see `docs/architecture.md`). Protocol code never
  depends on UI, rendering or platform APIs. The UI never talks to sockets directly.
- Prefer the .NET base class library (`System.Security.Cryptography`, `System.Net.Sockets`,
  `System.Buffers.Binary`, `System.IO.Pipelines`) before adding a package.
- Use `Span<T>`/`Memory<T>` and pooled buffers on hot paths (packet parsing, decryption,
  frame reassembly). Avoid allocations per packet.
- Async I/O with `CancellationToken` everywhere a network operation can block.
- Nullable reference types enabled; treat warnings as errors.
- Comments in English and only where they add information the code cannot express (for
  example, why a protocol quirk exists). No comments that restate the code.
- All documentation, identifiers, log messages and commit messages are in English.

## Repository layout

```
assets/               Source artwork (logo); the app icons in src/Cord.App/Assets/Icons are made from it
docs/                 Protocol and project documentation
src/                  Production projects (see docs/architecture.md)
tests/                Test projects, one per production project
```

## Workflow

- Keep commits focused, with messages that explain why.
- Run `dotnet build` and `dotnet test` before every commit.
- When behavior differs between the docs and a real console, the console wins: fix the docs.
