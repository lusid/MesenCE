# Mesen Community Edition

Mesen is a multi-system emulator for Windows, Linux, and macOS. It supports NES, SNES, Game Boy (GB/SGB/GBC), Game Boy Advance, PC Engine, SMS/Game Gear, and WonderSwan (WS/WSC).

This is a community-managed fork, created to maintain and expand this emulator into the future.

## Releases

The latest stable version is available from the [releases page on GitHub](https://github.com/nesdev-org/MesenCE/releases).

## Development Builds

[![Mesen](https://github.com/nesdev-org/MesenCE/actions/workflows/build.yml/badge.svg)](https://github.com/nesdev-org/MesenCE/actions/workflows/build.yml?query=branch%3Amaster)

* [Windows](https://nightly.link/nesdev-org/MesenCE/workflows/build/master/Mesen%20%28Windows%20-%20net10.0%20-%20AoT%29.zip)
  * Windows 7 or higher is required. Windows 7 users must use SP1 and have all updates installed.
* [Linux x64](https://nightly.link/nesdev-org/MesenCE/workflows/build/master/Mesen%20%28Linux%20-%20ubuntu-22.04%20-%20clang_aot%29.zip)  (requires **SDL2**)  
* [Linux ARM64](https://nightly.link/nesdev-org/MesenCE/workflows/build/master/Mesen%20%28Linux%20-%20ubuntu-22.04-arm%20-%20clang_aot%29.zip)  (requires **SDL2**)  
* [macOS - Intel](https://nightly.link/nesdev-org/MesenCE/workflows/build/master/Mesen%20%28macOS%20-%20macos-15-intel%20-%20clang_aot%29.zip)  (requires **SDL2**)  
* [macOS - Apple Silicon](https://nightly.link/nesdev-org/MesenCE/workflows/build/master/Mesen%20%28macOS%20-%20macos-15%20-%20clang_aot%29.zip)  (requires **SDL2**)  

#### <ins>Notes</ins> ####

* Other builds are also available in the [Actions](https://github.com/nesdev-org/MesenCE/actions/workflows/build.yml?query=branch%3Amaster) tab.
* **macOS**: Builds are self-signed and will require approval via Gatekeeper before they are able to be run.  
* **SteamOS**: See [SteamOS.md](SteamOS.md)  

## Compiling

See [COMPILING.md](COMPILING.md)

## MCP Server

This fork includes a local Model Context Protocol server for live emulator inspection and memory access.

1. Open **Options > Preferences > Advanced**.
2. Enable **MCP Server** and leave the port at `7342` unless the client requires another port.
3. Select **OK**, close Mesen, and restart it. Enabling, disabling, or changing the port takes effect after restart.
4. Configure the MCP client to use the Streamable HTTP endpoint `http://127.0.0.1:7342/mcp`.

The endpoint exposes exactly five tools:

- `get_emulator_status`: returns whether a game is loaded, the system, ROM filename without its path, run state, and Mesen/MCP versions.
- `list_memory_spaces`: returns the memory-space IDs, sizes, and read/write capabilities available for the loaded system.
- `read_memory`: reads up to 65,536 bytes from a memory space and returns `data` as base64 plus `hex` for display.
- `write_memory`: writes up to 65,536 bytes to a writable memory space; its `data` input is a base64 string.
- `get_cpu_registers`: returns the live CPU register set supported for the loaded system.

Pass numeric inputs such as `address` and `count` as decimal JSON numbers, not `$`-prefixed or `0x`-prefixed strings. Pass the case-sensitive enum-name `id` returned by `list_memory_spaces` as `space`; for example, use `NesInternalRam` only when discovery returns that ID. The `read_memory` and `write_memory` `data` field is base64 because the protocol model uses a JSON `byte[]`; it is not a numeric field.

All tool calls inspect the live emulator without pausing it. A multi-byte read or write is not an atomic snapshot relative to emulation, concurrent protocol calls are serialized, and writes modify emulator state immediately. Record the original value before a diagnostic write and restore that same value when finished.

The server binds only to `127.0.0.1`, but every local process and every MCP client granted access to it is inside the trust boundary and may invoke destructive writes. Server lifecycle and tool failures are written to Mesen's log with an `[MCP]` prefix.

### Build and Test

Install SDL2 and the .NET 10 SDK. From the repository root on Apple Silicon macOS, place the intended SDK first on `PATH` and run every gate from a clean native-core build:

```bash
export PATH=/path/to/dotnet10:$PATH
dotnet test UI.Tests/UI.Tests.csproj -c Release -p:RuntimeIdentifier=osx-arm64
make clean
make
make clean
USE_AOT=true make
```

Use `osx-x64` instead of `osx-arm64` on Intel macOS. The app is produced at `bin/<rid>/Release/<rid>/publish/Mesen.app`; the last command above leaves the Native AOT app in that location.

### Syncing Upstream

Keep `origin` pointed at this fork and `upstream` pointed at `https://github.com/nesdev-org/MesenCE.git`. Update the fork only after rebasing and repeating the complete test, normal-build, Native AOT build, and live MCP smoke test:

```bash
git switch master
git fetch origin upstream
git rebase upstream/master
dotnet test UI.Tests/UI.Tests.csproj -c Release -p:RuntimeIdentifier=osx-arm64
make clean
make
make clean
USE_AOT=true make
git push origin master
```

Resolve rebase conflicts without dropping MCP behavior. Do not update a consuming repository's submodule pointer until the rebased fork revision has passed all gates.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md)

## License

Mesen is available under the GPL V3 license.  Full text here: <http://www.gnu.org/licenses/gpl-3.0.en.html>

Copyright (C) 2014-2026 Sour, 2026 contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
