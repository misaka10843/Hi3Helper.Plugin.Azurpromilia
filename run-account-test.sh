#!/usr/bin/env bash
#
# run-account-test.sh — builds and runs the plugin's smoke tests.
#
# Usage:
#   ./run-account-test.sh            # offline checks only
#   ./run-account-test.sh --live     # also drive the real API client against the live server
#
# Covers the two things the plugin cannot get wrong:
#   1. The request signature formula. GET signs the canonical (key-sorted) query; POST signs the
#      exact JSON body. Getting this wrong makes the server answer "code=2, MD5校验错误", which
#      looks exactly like a bad password.
#   2. Account-file resolution: the account file lives in the game folder and only there, nothing
#      is written before the game folder is known, and re-resolving mutates the shared instance
#      in place rather than returning a new one.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="tests/AccountResolutionTests/AccountResolutionTests.csproj"
OUTDIR="$ROOT/tests/AccountResolutionTests/bin/Release/net10.0"

# This shell often ships without the Windows folder environment variables that MSBuild expects.
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

cd "$ROOT"

"$DOTNET" build "$PROJECT" \
    -c Release \
    -nodeReuse:false \
    -m:1 \
    -p:LocalNoNuGetBuild=true \
    -p:RestoreProjectStyle=Unknown \
    -p:SkipResolvePackageAssets=true \
    -p:GenerateDependencyFile=false \
    -p:GenerateRuntimeConfigurationFiles=false \
    -v:quiet

# The SDK's GenerateRuntimeConfigurationFiles task also insists on reading project.assets.json, which
# this build path deliberately never produces. Hand-write the equivalent file instead.
cat > "$OUTDIR/AccountResolutionTests.runtimeconfig.json" <<'JSON'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
    "configProperties": { "System.Globalization.Invariant": true }
  }
}
JSON

# The plugin declares Microsoft.Extensions.Logging.Abstractions with Private=false because Collapse
# supplies it at runtime. The harness has no host, so drop a copy next to the test assembly.
LOGGING_DLL="$HOME/.nuget/packages/microsoft.extensions.logging.abstractions/10.0.12/lib/net10.0/Microsoft.Extensions.Logging.Abstractions.dll"
if [ -f "$LOGGING_DLL" ]; then
    cp -f "$LOGGING_DLL" "$OUTDIR/"
else
    echo "WARNING: Microsoft.Extensions.Logging.Abstractions.dll not found in the NuGet cache;" >&2
    echo "         the test will fail to load the plugin assembly." >&2
fi

echo
# NOTE: cd into the output folder and use a bare file name — dotnet.exe on Windows treats a
# POSIX-style absolute path (/c/Users/...) as an invalid argument.
cd "$OUTDIR"
"$DOTNET" exec AccountResolutionTests.dll "$@"
