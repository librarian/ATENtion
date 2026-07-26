# ASPEED native decoder

This directory contains the ASPEED Technology reference decoder from
`AspeedTech-BMC/aspeed_codec`, adapted to:

- export a small native ABI (`aspeed_init`, `aspeed_decode`);
- compile without Emscripten; and
- set the alpha byte of decoded BGRA pixels to opaque;
- restore the ASPEED VQ block decoding used by YUV444 Enhanced Text streams; and
- reject truncated or unknown blocks without reading beyond the input buffer.

The upstream source files and these modifications are licensed under the
Mozilla Public License 2.0. See `LICENSE`. The package metadata in the upstream
repository says GPL-3.0-or-later, while the repository license and source-file
headers explicitly state MPL-2.0; this copy follows the license attached to the
source files.

Windows builds require LLVM/Clang. The checked-in build script creates the x64
DLL with reproducible linker output:

```powershell
./build-windows.ps1
```

Linux and macOS builds use the system C compiler:

```text
cc -shared -fPIC -O2 -std=c11 -o libaspeed_codec.so decoder.c
cc -dynamiclib -O2 -std=c11 -o libaspeed_codec.dylib decoder.c
```

Generated `.dll`, `.so`, and `.dylib` files are build artifacts and are not
tracked in Git.

Do not build this source with Zig 0.15.1: that compiler produced a DLL which
decoded only scattered macroblocks for valid AST2500 full frames. The same
captured packet decoded correctly with ASPEED's JavaScript reference decoder,
the Linux C build, and the LLVM/Clang Windows build.
