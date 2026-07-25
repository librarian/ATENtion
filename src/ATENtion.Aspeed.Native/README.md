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

The DLL is built for Windows x64 with:

```text
zig cc -target x86_64-windows-gnu -O2 -shared decoder.c -o aspeed_codec.dll
```
