<p align="center">
  <img src="DSHHarbor/Assets/dsh-harbor-logo.png" alt="DSH Harbor logo" width="180">
</p>

# DSH Harbor

[简体中文](README.zh-CN.md)

> [!IMPORTANT]
> This is an open-source project maintained by community contributors and built on DeepSeek Harness. **It is not an official DeepSeek product and does not represent the views or positions of DeepSeek.**

DSH Harbor is an unofficial Windows desktop launcher for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness). It installs and manages the local Harness runtime, starts the Web interface on a loopback port, and displays it in an integrated WebView2 window.

The goal is simple: make DeepSeek Harness feel like a regular Windows application—without asking users to install Node.js, configure pnpm, or manage terminal processes by hand.

## Highlights

- **One-click setup** — downloads and prepares Node.js and DeepSeek Harness on first launch.
- **Integrated Web UI** — runs the Harness Web interface locally and opens it inside a native WPF window.
- **Portable Node.js fallback** — uses a compatible system Node.js installation when available, or downloads a verified Node.js runtime automatically.
- **China-friendly package downloads** — installs npm packages through Alibaba Cloud's npmmirror registry.
- **Windows-compatible module layout** — places `DSH_HOME` below the hoisted Harness installation so Node can resolve real packages even when Windows blocks profile fallback junctions.
- **System tray support** — closing the window hides it to the tray; the tray menu can reopen or fully exit the application.
- **Visible bootstrap diagnostics** — shows installation and startup progress, with retry and log-folder shortcuts when something fails.
- **Clean process shutdown** — uses a Windows Job Object so exiting DSH Harbor also terminates the local Harness server and its child processes.
- **Safe update checks** — checks the npm mirror for a newer Harness version at most once per day while continuing to use the pinned, tested version.

## Requirements

- Windows 10 or Windows 11, x64-compatible
- Internet access during the first launch
- Administrator permission for the installer
- Microsoft Edge WebView2 Runtime
- .NET 10 Desktop Runtime

The installer detects WebView2 and the .NET Desktop Runtime and downloads them when required.

## Installation

1. Download the latest `DSHHarborSetup-*.exe` from the repository's **Releases** page.
2. Run the installer.
3. Start **DSH Harbor** from the Start menu or desktop shortcut.
4. Wait for the first-run bootstrap to finish. The application may restart itself once after installing Harness.

The initial bootstrap installs the currently tested Harness release, `@deepseek-ai/dsh@0.1.1-rc.2`. Later launches reuse the local installation.

> The installer is not currently Authenticode-signed. Windows SmartScreen may show an unknown-publisher warning until a code-signing certificate is added.

## How it works

```text
DSH Harbor
  ├─ selects a compatible system or bundled Node.js runtime
  ├─ installs a pinned DeepSeek Harness release with pnpm
  ├─ prepares the Windows-safe DSH_HOME module layout
  ├─ starts `dsh web` on an available 127.0.0.1 port
  └─ displays the local service in WebView2
```

The local Web server is bound to a dynamically selected loopback port. DSH Harbor waits for it to become ready before showing the browser view.

### Why is `DSH_HOME` inside the Harness directory?

DeepSeek Harness maintains a flat `profiles/node_modules` fallback using directory junctions. Some restricted or non-interactive Windows launch contexts can reject those junctions as untrusted mount points, causing every profile plugin to fail with `ERR_MODULE_NOT_FOUND`.

DSH Harbor stores the active home at `harness\home`. This puts the real hoisted `harness\node_modules` directory in Node.js's parent-directory search path, providing a junction-independent fallback while preserving a single physical copy of the Harness packages. Existing data from the older `dsh-home` location is migrated automatically.

## Local data and logs

DSH Harbor stores runtime files under:

```text
%LOCALAPPDATA%\DSHHarbor
```

Installations upgraded from the former **DSH Desktop** name continue using the legacy `%LOCALAPPDATA%\DSHDesktop` directory. This is intentional: it preserves existing profiles and avoids moving a live Harness installation. New installations use the DSH Harbor path above.

| Path | Purpose |
| --- | --- |
| `harness\` | The pnpm-managed DeepSeek Harness installation |
| `harness\home\` | Active `DSH_HOME`, profiles, and Harness user data |
| `runtime\` | Bundled Node.js runtime, when a compatible system Node.js is unavailable |
| `cache\` | Download cache |
| `logs\launcher.log` | Installer, bootstrap, and launcher diagnostics |
| `logs\dsh.log` | Harness process output and startup errors |
| `update-state.json` | Timestamp of the most recent Harness update check |

You can open the log directory directly from the startup error screen.

## Troubleshooting

### The window disappears when I close it

Closing the main window hides DSH Harbor to the system tray. Double-click the tray icon to reopen it. The tray menu can also check GitHub Releases for application updates, show an About dialog with the installed Harness and Node.js versions, or stop Harness completely with **Exit**.

### First launch fails

1. Use **Open log folder** on the error screen.
2. Check `launcher.log` first, then `dsh.log`.
3. Confirm that the npm mirror and Node.js download endpoints are reachable.
4. Click **Retry** after correcting the reported problem.

When reporting a bug, attach both log files and remove any secrets before publishing them.

### Harness plugins report `ERR_MODULE_NOT_FOUND`

Version 0.1.8 and later use the ancestor `node_modules` layout described above. If upgrading from an older build, install the latest release and allow DSH Harbor to migrate the old home directory automatically.

## Building from source

### Prerequisites

- Visual Studio 2022 or the .NET 10 SDK with Windows desktop tooling
- Microsoft Edge WebView2 Runtime
- Windows x64 for building the provided installer configuration

### Build and run

```powershell
dotnet build .\DSHHarbor\DSHHarbor.csproj
dotnet run --project .\DSHHarbor\DSHHarbor.csproj
```

### Create a release build

```powershell
dotnet publish .\DSHHarbor\DSHHarbor.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\artifacts\win-x64
```

### Build the installer

The repository includes the Inno Setup command-line compiler used by the current packaging workflow:

```powershell
.\tools\InnoSetup\ISCC.exe .\installer\DSHHarbor.iss
```

The generated installer is written to `artifacts\installer\`.

## Project structure

| Directory | Description |
| --- | --- |
| `DSHHarbor/` | WPF application and runtime bootstrap logic |
| `installer/` | Inno Setup installer definition |
| `tools/InnoSetup/` | Installer compiler used by the packaging workflow |
| `artifacts/` | Local publish and installer outputs; not source code |

## Updating the bundled Harness version

Harness is intentionally pinned in `DSHHarbor/DshRuntime.cs`. Before changing it:

1. Update the pinned package version.
2. Test a completely clean first launch.
3. Test both a new `%LOCALAPPDATA%\DSHHarbor` directory and an upgrade using the legacy `%LOCALAPPDATA%\DSHDesktop` directory.
4. Verify Web UI startup, tray exit, retry behavior, and both log files.
5. Publish a new installer version.

The daily registry check is informational in the current release; it does not silently replace the tested package.

## Contributing

Bug reports and pull requests are welcome. For startup issues, include:

- Windows version
- DSH Harbor version
- Whether Node.js was detected or downloaded
- `launcher.log` and `dsh.log`, with credentials and personal data removed
- Reproduction steps from a clean or upgraded installation

Please keep changes focused and verify both first-run installation and subsequent launches.

## License

DSH Harbor is released under the [MIT License](LICENSE).

## Acknowledgements

- [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)
- [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
- [pnpm](https://pnpm.io/)
- [Inno Setup](https://jrsoftware.org/isinfo.php)

## Disclaimer

DSH Harbor is an open-source project maintained by community contributors and built on DeepSeek Harness. It is not an official DeepSeek product, is not endorsed by DeepSeek, and does not represent the views or positions of DeepSeek. DeepSeek and DeepSeek Harness are trademarks or projects of their respective owners.
