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

## ⚠️ Read this first: you must supply your own login

Azur Promilia will not hand out download URLs to an anonymous client. The official launcher logs you
in through a **web flow guarded by GeTui / Geetest-style CAPTCHAs**, which a launcher plugin has no way
to host.

So this plugin does **not** try to automate login. Instead it reads credentials from a plain JSON file
that **you** control — and that file lives **in the game folder**, right where you already browse:

```
<game install folder>\account.json
```

A template is written there automatically the first time the launcher tells the plugin where the game
is installed. Fill in **one** of the strategies below, then restart the launcher.

### Where exactly does the plugin look?

**The game install folder, and nowhere else:**

```
<game install folder>\account.json
```

There is no copy under `%LOCALAPPDATA%`, and the plugin does not read one even if you happen to have
left a file there — that location is deliberately not used, precisely because it is hard to find.

Two details worth knowing:

* The plugin **does not write anything until it knows the game folder**. On a brand-new setup you will
  see nothing until you have pointed Collapse at the game directory once.
* Every run logs the file it used (`[AzurPromilia] Loaded account configuration from '...'`), so if you
  ever lose track of it, the launcher log will tell you. Deleting the file and restarting regenerates
  the template.

### Strategy 1 — paste an existing token (simplest)

```json
{
  "utoken": "<your session token>",
  "uid": "<your account id>",
  "accountType": 2
}
```

This never needs your password. `uid` and `accountType` are refreshed automatically once a login
succeeds; you only have to get `utoken` right.

### Strategy 2 — reuse the official launcher's token (default, `true`)

```json
{
  "importFromOfficialLauncher": true
}
```

If you have the official launcher installed and logged in **on the same machine**, the plugin decrypts
the token it already stored locally and reuses it. No credentials required. This is on by default, so
in the happy path you do not need to edit the file at all.

### Strategy 3 — account + password

```json
{
  "account": "13800000000",
  "password": "your-password",
  "accountType": 2
}
```

`account` may be a phone number or an e-mail address. The password is encrypted with the launcher's
own `extApi4` scheme before being sent, and is **never** written back to disk — after the first
successful login the plugin strips the `password` field from the file for you.

### Strategy 4 — phone + SMS code

```json
{
  "phone": "13800000000",
  "smsCode": "123456"
}
```

Because this plugin cannot display the SMS challenge, you must obtain the code yourself (e.g. from the
official launcher or the official website) and paste it in. SMS codes expire quickly, so this strategy
is mostly useful for one-off setup.

### If login fails, read the error code before touching your credentials

The passport API answers `code=2, "MD5校验错误"` when the **request signature** does not verify. That
message has nothing to do with your password — the request is rejected before the server ever looks at
the account. Treat the codes as follows:

| Server answer | Meaning | What to do |
| --- | --- | --- |
| `code=2` `MD5校验错误` | The plugin built the signed request wrongly | **Plugin bug.** Report it — your credentials are not the problem. |
| HTTP `404` | The request went to the wrong domain | Login must go to `passport-grp.manjuu.com`, not the business domain. Also a plugin bug. |
| `code=11017` `通行证不存在` | Signature accepted; the account does not exist | Check the `account` value in `account.json`. |
| Any other `code` | Signature accepted; genuine business rejection | The `msg` field is authoritative — wrong password, risk control, ban, etc. |

So: **any error other than `code=2`/`404` means the signature is fine and the problem really is the
account.** The plugin now logs `code=2` as an explicit error rather than a generic "no usable session"
warning, precisely so this distinction is obvious.

### Optional: `launcherRoot`

```json
{
  "launcherRoot": "D:\\Games\\AzurPromiliaLauncher"
}
```

Points at the official launcher's install directory (the one containing `res/app_config.dat`). Set this
if the plugin cannot auto-detect the launcher and you want it to re-read the current channel keys
instead of using the values compiled into the plugin.

### Full template

```json
{
  "utoken": null,
  "uid": null,
  "accountType": null,
  "account": null,
  "password": null,
  "phone": null,
  "smsCode": null,
  "importFromOfficialLauncher": true,
  "launcherRoot": null,
  "_readme": "Fill in ONE strategy. See README.md."
}
```

The file lives in the game install folder and nowhere else — see
[Where exactly does the plugin look?](#where-exactly-does-the-plugin-look).

### Verifying the lookup logic and the request signature

The rules above are covered by a runnable smoke test:

```bash
./run-account-test.sh          # offline: 33 assertions
./run-account-test.sh --live   # also drives the real API client against the live server
```

Offline it exercises a real `AzurPromiliaAccount` against temporary directories — template placement,
the "nothing before a game path" rule, in-place reload, save target, and the fact that leftover files
under `%LOCALAPPDATA%` or next to the plugin DLL are ignored — plus known-answer vectors pinning the
signing formula (`GET` signs the canonical query, `POST` signs the exact JSON body). It prints one
`PASS`/`FAIL` line per assertion (33 in total).

With `--live` it additionally uses the real `AzurPromiliaApiClient` to call `getLauncherConfig` and
then `pwdlogin` with a deliberately non-existent account, asserting the server does **not** answer
`code=2` — i.e. that the signature is accepted and only the account is rejected. That is the
end-to-end guard against the failure mode described above.

For a lower-level look at the signature scheme itself, `tools/probe/probe_sign.py` replays the same
requests with several signing variants and prints what the server accepts; see
[docs/03-网络层与API协议.md](./docs/03-网络层与API协议.md) §9.

---

## ✨ Features

| Feature | Status |
| --- | --- |
| Version detection | ✅ |
| Background images / banners / news | ✅ |
| Game download & install (SLICE + REALITY) | ✅ |
| Incremental file-level updates | ✅ |
| Integrity verification & repair | ✅ |
| Game launching (session injection) | ✅ |
| Process detection / kill | ✅ |
| Pre-load | ⚠️ implemented, unverified against a real pre-load window |
| Self-update | ⚠️ URLs are placeholders — see below |
| Manual integrity check | ❌ Collapse does not expose a UI entry point for plugins |

### How launching works

The game client takes its session **only** from two environment variables, which the plugin injects
when it starts the process:

| Variable | Contents |
| --- | --- |
| `manjuu_user_info` | `{"uid":...,"utoken":...,"accountType":...}` |
| `manjuu_channel_info` | `{"channelCode":...,"subChannelCode":...,"initTraceId":...,"currentTraceId":...,"previousTraceId":...}` |

No environment variables, no session — the game will bounce you straight back to the login screen.

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
4. Point Collapse at (or create) the game folder, then start the launcher once — `account.json` is
   created **inside that game folder**. Edit it if you are not relying on the official-launcher token
   import, then restart again.

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
│  ├─ AzurPromiliaGameManager.cs      Session bootstrap, version discovery, index download
│  ├─ AzurPromiliaGameInstaller.cs    Installer surface (size / install / update / repair)
│  ├─ AzurPromiliaGameInstaller.Install.cs   Download worker pool, SLICE merge, extract, diff
│  ├─ AzurPromiliaGameIndex.cs        SLICE + REALITY CSV index parsing
│  ├─ PresetConfig/
│  │  └─ AzurPromiliaCnPresetConfig.cs
│  └─ Api/
│     ├─ AzurPromiliaApiHelper.cs     Signed HTTP client (canonical query + header signature)
│     ├─ AzurPromiliaApiContext.cs    System.Text.Json source-generation context
│     ├─ AzurPromiliaApiStructs.cs    Wire-format DTOs
│     ├─ AzurPromiliaLoginService.cs  utoken / import / password / SMS resolution order
│     ├─ AzurPromiliaLauncherApiMedia.cs
│     └─ AzurPromiliaLauncherApiNews.cs
└─ Utils/
   ├─ AzurPromiliaCrypto.cs           Recovered extApi1–4 + resource-index cipher
   ├─ AzurPromiliaChannelConfig.cs    launcherId / appId / channelCode / secretKey resolution
   ├─ AzurPromiliaDeviceInfo.cs       device-id, headers, client-info
   ├─ AzurPromiliaAccount.cs          account.json credential store (game folder only)
   └─ AzurPromiliaImageData.cs        Embedded plugin artwork

tests/
└─ AccountResolutionTests/            Console smoke test for the credential-file lookup rules
```

The `docs/` directory at the repository root documents the reverse-engineering work behind the
protocol implementation — start at `docs/README.md`.

---

## ⚠️ Disclaimer

This is a third-party, unofficial plugin. It reimplements the *client-side* behaviour of the official
Azur Promilia launcher for interoperability purposes only. It contains no game assets, no game code,
and no account data. You are responsible for complying with the game's terms of service and any
applicable law in your jurisdiction. Use at your own risk.
