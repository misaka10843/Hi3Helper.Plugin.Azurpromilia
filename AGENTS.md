# Repository Guidance

## Commits

- Validate the change before committing. Write commit subjects and bodies in English.
- Use `feat (module): description`, for example `feat (download): stage game preload files`.
- Group changes by complete functionality and review the staged diff.
- Keep build outputs, local settings, downloaded game data, official launcher files and unrelated lock-file changes out of Git.
- Push and publish only when requested.

## Code

- Use the existing C# file-scoped namespaces, nullable references, four-space indentation and braces.
- Keep plugin ABI exports compatible with Core. Use source-generated JSON on Native AOT paths.
- Keep `Hi3Helper.Plugin.Core` and `SevenZipExtractor` as Git submodules pinned to upstream commits.
- Keep signed launcher API requests separate from CDN downloads. Validate package hashes and paths before promoting files.
- Keep preloads under `_pre_download/<version>`. Only write installed version metadata after a successful installation or update.
- Use comments to explain protocol requirements or decisions that are hard to infer from the code.

## Validation

- Build `Hi3Helper.Plugin.Azurpromilia.sln` in Release for x64.
- Run `dotnet run --project tests/PublicDownloadTests -c Release`. Use `-- --live` to check official package discovery and indexes.
- Publish with `ReleaseNoReflectionPublish-O1` to validate Native AOT. Record launcher and game acceptance separately from build checks.
- Run `.github/scripts/Test-PluginRelease.ps1` and `actionlint` after workflow changes.
- Build releases at the dispatch SHA. Keep PDBs in a separate artifact, validate the indexed payload, and keep stable tags tied to their original build commit.
