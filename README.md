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

This fork includes a local Model Context Protocol server for live emulator inspection, memory access, and debugger control.

1. Open **Options > Preferences > Advanced**.
2. Enable **MCP Server** and leave the port at `7342` unless the client requires another port.
3. Select **OK**, close Mesen, and restart it. Enabling, disabling, or changing the port takes effect after restart.
4. Configure the MCP client to use the Streamable HTTP endpoint `http://127.0.0.1:7342/mcp`.

The endpoint reports MCP feature version `2.0` through `get_emulator_status` and exposes 33 tools:

- Status and memory: `get_emulator_status`, `list_memory_spaces`, `read_memory`, `write_memory`, and `get_cpu_registers`.
- MCP-owned breakpoints: `set_breakpoint`, `list_breakpoints`, `remove_breakpoint`, and `remove_all_breakpoints`.
- Execution control: `pause`, `resume`, `step`, and `continue_until_break`.
- Debugger inspection: `get_break_context`, `disassemble`, `map_address`, and `get_call_stack`.
- Bounded tracing: `configure_execution_trace` and `get_execution_trace`.
- Automation discovery and evidence: `get_automation_capabilities` and `capture_screenshot`.
- In-memory save states: `create_save_state`, `load_save_state`, and `delete_save_state`.
- Deterministic execution: `run_experiment`.
- Stopped-state snapshots: `create_memory_snapshot`, `compare_memory_snapshots`, and `delete_memory_snapshot`.
- Iterative memory search: `start_memory_search`, `refine_memory_search`, `get_memory_search_results`, `undo_memory_search`, and `delete_memory_search`.

Breakpoint access is `execute`, `read`, or `write`. Step types are `instruction`, `over`, `out`, `cpu_cycle`, `ppu_scanline`, `ppu_frame`, and `back`, subject to active-system support. Trace actions are `enable`, `configure`, `clear`, and `disable`.

Pass numeric inputs such as `address`, `count`, and every `data` byte as decimal JSON numbers, not `$`-prefixed or `0x`-prefixed strings. Each write byte must be an integer from `0` through `255`; for example, two bytes are `"data": [0, 255]`. Pass the case-sensitive enum-name `id` returned by `list_memory_spaces` as `space`; for example, use `NesInternalRam` only when discovery returns that ID.

Memory and inspection calls do not pause automatically. `pause`, `resume`, `step`, and `continue_until_break` intentionally change execution state. Break context is valid only while stopped at the same emulator generation; resume, reset, ROM transitions, and state loads invalidate it. A multi-byte read or write is not an atomic snapshot relative to emulation, concurrent protocol calls are serialized, and writes modify emulator state immediately. Record the original value before a diagnostic write and restore that same value when finished.

MCP breakpoints use stable IDs and coexist with debugger UI breakpoints. The in-memory execution trace has one owner at a time, so MCP returns `operation_in_progress` rather than overwriting an active UI trace. Limits include 128 MCP breakpoints, 256 disassembly rows, 128 call-stack frames, 1,000 returned trace rows, 999 UTF-8 bytes per condition or trace format, and a 30-second maximum `continue_until_break` timeout.

Call `get_automation_capabilities` after loading a game and treat its response as authoritative. It reports the active system and ROM identity, mutable-state generation, supported save-state, screenshot, and deterministic-frame operations, exact frame semantics, configured controller ports and controls, exclusive-input support, resource and experiment limits, and system-specific limitations. The public schemas are platform-neutral, but NES is the first fully certified deterministic adapter. Unsupported systems or configured devices reject unavailable automation with `unsupported_system` or `unsupported_capability`; they never silently substitute weaker input or timing behavior.

`run_experiment` holds one execution lease from optional state restoration through cleanup. It applies complete exclusive controller state, advances frames through correlated debugger stops rather than wall-clock sleeps, captures coherent stopped-state checkpoints, evaluates bounded memory assertions, and can return a final PNG. Controlled buttons do not carry between segments, omitted controlled ports are neutral, and host input is restored during cleanup. Successful runs remain stopped for deterministic follow-up. Breakpoints are not bypassed: an interruption returns partial checkpoints, completed frames, break context, skipped work, final run state, and cleanup status. Runtime outcomes use `completed`, `assertion_failed`, `interrupted`, or `failed`; preflight validates the entire request before changing emulator state.

Save states, snapshots, and searches are in-memory resources owned only by the current MCP server instance and identified by opaque IDs; no tool accepts an arbitrary state or screenshot path. Save-state loads preserve compatible snapshots and searches but invalidate break context, mappings, and execution trace state. Snapshots capture stopped memory and compare compatible ranges as paged changed runs. Searches retain ascending candidates and one undo level, and compatible searches survive save-state loads so contrasting refinements are possible.

Resource creation fails rather than evicting another resource. Limits are 8 save states at 16 MiB each and 64 MiB aggregate; 16 snapshots at 16 MiB each and 64 MiB aggregate; 8 searches over at most 16 MiB each, 40 MiB allocation per search and 160 MiB aggregate; and result pages of at most 1,000. Experiments allow 256 segments, 3,600 total frames, 256 observations, 256 assertions, 65,536 observed bytes, and a 1 through 300,000 ms timeout. Screenshots are limited to an 8 MiB raw PNG, 4,096 pixels per dimension, and 16,777,216 pixels total.

Save states, snapshots, and searches expire after 30 minutes of inactivity measured by a monotonic clock; successful use refreshes the deadline and active operations pin their resources. Explicit deletion removes metadata immediately. ROM replacement or unload invalidates resource data but retains stale metadata until expiration so access returns `stale_resource`; deleted, unknown, and expired IDs return `resource_not_found`. Server shutdown disposes all resources and releases owned input.

`capture_screenshot` returns structured dimensions, frame number, PNG byte count, ROM identity, and mutable-state generation plus a separate MCP `image` content block with MIME type `image/png`; structured JSON never embeds the base64 payload. A successful `run_experiment` with final capture uses the same image-block convention.

If experiment cleanup cannot confirm a stopped or lifecycle state, execution becomes quarantined and status reports `unknown`. Read-only inspection remains available, but `resume`, `step`, `continue_until_break`, and `run_experiment` return `execution_state_unknown`. `pause` remains available for recovery; a confirmed pause, debugger stop, reset, ROM load, or ROM unload clears quarantine.

The server binds only to `127.0.0.1`, but every local process and every MCP client granted access to it is inside the trust boundary and may invoke destructive writes. Server lifecycle and tool outcomes are written to Mesen's log with an `[MCP]` prefix, request type, result, and duration; arguments and memory payloads are never logged.

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

Keep `origin` pointed at this fork and `upstream` pointed at `https://github.com/nesdev-org/MesenCE.git`. Update the fork only after merging upstream and repeating the complete test, normal-build, Native AOT build, and live MCP smoke test:

```bash
git switch master
git fetch upstream
git merge upstream/master
dotnet test UI.Tests/UI.Tests.csproj -c Release -p:RuntimeIdentifier=osx-arm64
make clean
make
make clean
USE_AOT=true make
git push origin master
```

Resolve merge conflicts without dropping MCP behavior. Do not update a consuming repository's submodule pointer until the merged fork revision has passed all gates.

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
