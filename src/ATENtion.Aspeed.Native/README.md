# ASPEED native decoder

This directory contains the ASPEED Technology reference decoder from
`AspeedTech-BMC/aspeed_codec`, adapted only to:

- export a small native Windows ABI (`aspeed_init`, `aspeed_decode`);
- compile without Emscripten; and
- set the alpha byte of decoded BGRA pixels to opaque.

The upstream source files and these modifications are licensed under the
Mozilla Public License 2.0. See `LICENSE`. The package metadata in the upstream
repository says GPL-3.0-or-later, while the repository license and source-file
headers explicitly state MPL-2.0; this copy follows the license attached to the
source files.

The DLL is built reproducibly for Windows x64 with llvm-mingw 20260616:

```text
x86_64-w64-mingw32-gcc -shared -O2 -std=c11 -s \
  -Wl,--no-insert-timestamp -o aspeed_codec.dll decoder.c
```

Do not build this source with Zig 0.15.1: that compiler produced a DLL which
decoded only scattered macroblocks for valid AST2500 full frames. The same
captured packet decoded correctly with ASPEED's JavaScript reference decoder,
the Linux C build, and the llvm-mingw Windows build.
