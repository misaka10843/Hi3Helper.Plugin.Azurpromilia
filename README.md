# Hi3Helper.Plugin.Azurpromilia

A third-party Collapse Launcher plugin for Azur Promilia (蓝色星原：旅谣) on Windows x64.

The plugin discovers public packages without a plugin account, installs from SLICE or REALITY packages, and verifies files during updates. When the server publishes a distinct preload version, the plugin stages changed files under `_pre_download/<version>` and reuses them after the version becomes available for installation.

## Build and install

Install the .NET 10 SDK and the Visual Studio **Desktop development with C++** workload for Native AOT.

```powershell
git submodule update --init --recursive
dotnet build Hi3Helper.Plugin.Azurpromilia.sln -c Release -p:Platform=x64
dotnet publish Hi3Helper.Plugin.Azurpromilia -c ReleaseNoReflection -r win-x64 -p:PublishProfile=ReleaseNoReflectionPublish-O1 -o publish_output
Get-ChildItem publish_output -Filter *.pdb -Recurse -File | Remove-Item
./Indexer.exe ./publish_output
```

In Collapse Launcher, open **Settings → Plugin Management**, import the generated ZIP or manifest, and restart the launcher. Select the game directory in Collapse Launcher.

## Check package discovery

```powershell
dotnet run --project tests/PublicDownloadTests -c Release
dotnet run --project tests/PublicDownloadTests -c Release -- --live
```

The live check downloads the indexes and verifies one small CDN file. An installed official launcher can be selected explicitly using `--launcher-root` followed by its version directory.

## Releases

**Release Azur Promilia** publishes the indexed ZIP at `AzurPromilia@v<version>` and opens a release-repository PR using `RELEASE_REPO_TOKEN`. **Pre-Release Azur Promilia** updates a dedicated pre-release tag and distributes its ZIP through GitHub Releases.
