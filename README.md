<div align="center">

# Hi3Helper.Plugin.Azurpromilia

A third-party [Collapse Launcher](https://collapselauncher.com/) plugin that adds support for
**Azur Promilia** (《蓝色星原：旅谣》, Manjuu / 蛮啾网络) — installation, integrity verification,
incremental updates and game launching.

</div>

---

> [!IMPORTANT]
> This plugin is **not** affiliated with, endorsed by, or maintained by Manjuu, Azur Promilia, or the
> Collapse Launcher team. Do not report issues with this plugin to the official Collapse repository or
> Discord — open an issue here instead.

---

## Public downloads

Installation, updates and repair use the public launcher API without a plugin login or an `account.json` file. Existing account files are ignored. Sign in inside the game after launching it.

---

## ✨ Features

| Feature | Status |
| --- | --- |
| Version detection | ✅ |
| Background images / banners / news | ✅ |
| Game download & install (SLICE + REALITY) | ✅ |
| Incremental file-level updates | ✅ |
| Integrity verification & repair | ✅ |
| Game launching (sign in inside the game) | ✅ |
| Process detection / kill | ✅ |
| Pre-load | ⚠️ implemented, unverified against a real pre-load window |
| Self-update | ⚠️ URLs are placeholders — see below |
| Manual integrity check | ❌ Collapse does not expose a UI entry point for plugins |

### How launching works

The plugin passes channel metadata and an empty user session to the game. Authentication is handled inside the game.

---

## 🔧 Building

### Prerequisites

* .NET 10 SDK
* The **Desktop development with C++** workload (MSVC linker) — required only for the Native AOT
  publish step, not for a plain `dotnet build`

Submodules (`Hi3Helper.Plugin.Core`, `SevenZipExtractor`) must be checked out:

```bash
git clone --recursive https://github.com/<you>/Hi3Helper.Plugin.Azurpromilia.git
# or, in an existing clone:
git submodule update --init --recursive
```

### Normal build

```bash
dotnet build Hi3Helper.Plugin.Azurpromilia.sln -c Release
```

### Public download regression check

```bash
dotnet run --project tests/PublicDownloadTests -c Release
# Also check anonymous package discovery against the real server:
dotnet run --project tests/PublicDownloadTests -c Release -- --live
```

The offline check uses simulated API responses. The live check discovers package URLs; it does not download or launch the full game.

### Sandboxed / offline build

If `dotnet build` fails with:

```
error : Value cannot be null. (Parameter 'path1')
   at NuGet.Configuration.XPlatMachineWideSetting..ctor()
```

…your environment is missing the Windows shell-folder state that NuGet's machine-wide settings
resolver depends on (common in stripped-down containers, CI images and sandboxed shells). Use the
bundled NuGet-free path instead:

```bash
./build-local.sh Release          # or: ReleaseNoReflection / Debug / DebugNoReflection
```

This passes `RestoreProjectStyle=Unknown`, `SkipResolvePackageAssets=true` and
`GenerateDependencyFile=false` so the build never touches NuGet, and satisfies the single package the
tree needs (`Microsoft.Extensions.Logging.Abstractions`) from a local `Reference` declared in
`Directory.Build.props`. See that file for the full explanation. **`Directory.Build.props` is inert
unless `LocalNoNuGetBuild=true` is passed**, so a normal build is unaffected.

### Native AOT publish

Publish profiles live in `Hi3Helper.Plugin.Azurpromilia/Properties/PublishProfiles/`:

| Profile | Configuration | Optimisation | Reflection |
| --- | --- | --- | --- |
| `ReleasePublish-O1` | Release | Size | enabled |
| `ReleasePublish-O2` | Release | Speed | enabled |
| `ReleaseNoReflectionPublish-O1` | ReleaseNoReflection | Size | **disabled** |
| `ReleaseNoReflectionPublish-O2` | ReleaseNoReflection | Speed | **disabled** |
| `DebugPublish` | Debug | none | enabled |
| `DebugNoReflectionPublish` | DebugNoReflection | none | **disabled** |

```bash
dotnet publish Hi3Helper.Plugin.Azurpromilia/Hi3Helper.Plugin.Azurpromilia.csproj \
    -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O1 -o out/
```

Or use the wrapper, which also handles symbol stripping and (optionally) manifest indexing:

```
CompileAOTAndShip.bat        # interactive menu
CompileAOTAndShip.bat 1      # Size / reflection enabled
```

> Reflection-free builds are experimental. Prefer `ReleasePublish-O1` unless you are chasing binary
> size.

### Before you ship

`SelfUpdate.cs` contains **placeholder CDN URLs**
(`https://github.com/Hi3Helper-PLUGINS/Hi3Helper.Plugin.Azurpromilia/...`). Point them at your own
hosting, or delete the `GetPluginSelfUpdater` override in `Plugin.cs` to disable self-update entirely.

---

## 📦 Installing into Collapse Launcher

1. Build/publish as above and package the output with the Collapse plugin Indexer to produce a
   `manifest.json` + `.zip`.
2. In Collapse: **Settings → Open Plugin Management Menu → Click to add .zip or manifest.json**.
3. Select the archive and **restart the launcher**.
4. Point Collapse at (or create) the game folder and start the download. No plugin login is required.

Requires Collapse Launcher `1.83.14` or newer (plugin API v0.1.5 standard).

---

## 🗂️ Source map

```
Hi3Helper.Plugin.Azurpromilia/
├─ Plugin.cs                          Plugin metadata + preset registration
├─ Exports.cs                         SharedStaticV1Ext ABI entry point (TryGetApiExport)
├─ Exports.GameLaunch.cs              Process launch, env-var session injection, log tailing
├─ SelfUpdate.cs                      Plugin self-update source
├─ AssemblyInfo.cs                    Assembly-level SupportedOSPlatform("windows")
├─ Management/
│  ├─ AzurPromiliaGameManager.cs      Public version discovery, index download
│  ├─ AzurPromiliaGameInstaller.cs    Installer surface (size / install / update / repair)
│  ├─ AzurPromiliaGameInstaller.Install.cs   Download worker pool, SLICE merge, extract, diff
│  ├─ AzurPromiliaGameIndex.cs        SLICE + REALITY CSV index parsing
│  ├─ PresetConfig/
│  │  └─ AzurPromiliaCnPresetConfig.cs
│  └─ Api/
│     ├─ AzurPromiliaApiHelper.cs     Signed HTTP client (canonical query + header signature)
│     ├─ AzurPromiliaApiContext.cs    System.Text.Json source-generation context
│     ├─ AzurPromiliaApiStructs.cs    Wire-format DTOs
│     ├─ AzurPromiliaLauncherApiMedia.cs
│     └─ AzurPromiliaLauncherApiNews.cs
└─ Utils/
   ├─ AzurPromiliaCrypto.cs           Request signing + configuration/resource-index ciphers
   ├─ AzurPromiliaChannelConfig.cs    launcherId / appId / channelCode / secretKey resolution
   ├─ AzurPromiliaDeviceInfo.cs       device-id, headers, client-info
   └─ AzurPromiliaImageData.cs        Embedded plugin artwork

```

The `docs/` directory at the repository root documents the reverse-engineering work behind the
protocol implementation — start at `docs/README.md`.

---

## ⚠️ Disclaimer

This is a third-party, unofficial plugin. It reimplements the *client-side* behaviour of the official
Azur Promilia launcher for interoperability purposes only. It contains no game assets, no game code,
and no account data. You are responsible for complying with the game's terms of service and any
applicable law in your jurisdiction. Use at your own risk.
