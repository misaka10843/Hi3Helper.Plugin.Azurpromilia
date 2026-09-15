#!/usr/bin/env bash
#
# build-local.sh — build the Azur Promilia plugin on machines where NuGet restore
# cannot run (see Directory.Build.props for the full explanation).
#
# Usage:
#   ./build-local.sh                    # Release
#   ./build-local.sh ReleaseNoReflection
#   ./build-local.sh Debug
#
set -euo pipefail

CONFIG="${1:-Release}"
[ $# -gt 0 ] && shift
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# NOTE: keep this a *relative* path. MSBuild on Windows interprets anything that
# starts with a forward slash as a command-line switch, so POSIX-style absolute
# paths (e.g. /c/Users/...) are rejected. We cd into $ROOT below instead.
PROJECT="Hi3Helper.Plugin.Azurpromilia/Hi3Helper.Plugin.Azurpromilia.csproj"

# The sandboxed shell this is often run from ships without the Windows folder
# environment variables that NuGet/msbuild expect. Re-export sane values so that
# anything that does consult them still works.
export APPDATA="${APPDATA:-C:\\Users\\$USERNAME\\AppData\\Roaming}"
export LOCALAPPDATA="${LOCALAPPDATA:-C:\\Users\\$USERNAME\\AppData\\Local}"
export ProgramData="${ProgramData:-C:\\ProgramData}"
export ALLUSERSPROFILE="${ALLUSERSPROFILE:-C:\\ProgramData}"
export USERPROFILE="${USERPROFILE:-C:\\Users\\$USERNAME}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet.exe}"
if [ ! -x "$DOTNET" ]; then
    DOTNET="$(command -v dotnet)"
fi

echo "==> Configuration : $CONFIG"
echo "==> dotnet        : $DOTNET"
echo "==> project       : $ROOT/$PROJECT"

cd "$ROOT"

"$DOTNET" build "$PROJECT" \
    -c "$CONFIG" \
    -nodeReuse:false \
    -m:1 \
    -p:LocalNoNuGetBuild=true \
    -p:RestoreProjectStyle=Unknown \
    -p:SkipResolvePackageAssets=true \
    -p:GenerateDependencyFile=false \
    "$@"
