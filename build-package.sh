#!/usr/bin/env bash
# Build the VMatrix.Tracing.Sdk NuGet package for manual upload to nuget.org.
#
# Usage: ./build-package.sh [--version X.Y.Z] [--skip-tests] [--allow-dirty]
#
#   --version X.Y.Z  package version (default: <Version> in the .csproj).
#                    nuget.org never lets a version be replaced, so every
#                    upload needs a new one.
#   --skip-tests     do not run the test suite first
#   --allow-dirty    build even with uncommitted or unpushed changes (the
#                    package then records a commit that does not match it)
#
# Produces a single artifacts/VMatrix.Tracing.Sdk.<version>.nupkg. Debug
# symbols are embedded in the DLL, so Source Link works without a .snupkg.
set -euo pipefail

cd "$(dirname "$0")"

PROJECT=src/Tracing.Sdk/Tracing.Sdk.csproj
ARTIFACTS=artifacts
VERSION=""
SKIP_TESTS=false
ALLOW_DIRTY=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    --skip-tests) SKIP_TESTS=true; shift ;;
    --allow-dirty) ALLOW_DIRTY=true; shift ;;
    -h|--help) sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1 (see --help)" >&2; exit 2 ;;
  esac
done

fail() { echo "error: $*" >&2; exit 1; }

if [[ -n "$VERSION" && ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  fail "--version must look like 1.2.3 or 1.2.3-beta.1, got \"$VERSION\""
fi

# dotnet on PATH, or a user-local install from dotnet-install.sh.
DOTNET=$(command -v dotnet || true)
[[ -z "$DOTNET" && -x "$HOME/.dotnet/dotnet" ]] && DOTNET="$HOME/.dotnet/dotnet"
[[ -n "$DOTNET" ]] || fail "dotnet not found; install the .NET 10 SDK"
"$DOTNET" --list-sdks | grep -q '^10\.' || fail "the .NET 10 SDK is required ($DOTNET has: $("$DOTNET" --list-sdks | cut -d' ' -f1 | tr '\n' ' '))"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# The package records the current commit for Source Link, so it must be
# committed and pushed, or debuggers fetch sources that do not match.
if [[ "$ALLOW_DIRTY" == false ]]; then
  [[ -z "$(git status --porcelain)" ]] || fail "uncommitted changes; commit them, or pass --allow-dirty"
  git fetch --quiet origin || fail "cannot reach the git remote to check the commit is pushed"
  git merge-base --is-ancestor HEAD '@{upstream}' 2>/dev/null \
    || fail "commit $(git rev-parse --short HEAD) is not pushed; push it, or pass --allow-dirty"
fi

if [[ "$SKIP_TESTS" == false ]]; then
  echo "==> Running tests"
  "$DOTNET" test --nologo --verbosity quiet
fi

echo "==> Packing"
rm -rf "$ARTIFACTS"
PACK_ARGS=(
  -c Release
  -o "$ARTIFACTS"
  -p:ContinuousIntegrationBuild=true
  -p:IncludeSymbols=false
  -p:DebugType=embedded
)
[[ -n "$VERSION" ]] && PACK_ARGS+=("-p:Version=$VERSION")
"$DOTNET" pack "$PROJECT" "${PACK_ARGS[@]}" --nologo --verbosity quiet

shopt -s nullglob
PACKAGES=("$ARTIFACTS"/*.nupkg)
[[ ${#PACKAGES[@]} -eq 1 ]] || fail "expected one .nupkg in $ARTIFACTS, found ${#PACKAGES[@]}"
PACKAGE=${PACKAGES[0]}

NUSPEC=$(unzip -p "$PACKAGE" '*.nuspec')
field() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" <<<"$NUSPEC"; }
COMMIT=$(sed -n 's/.*commit="\([0-9a-f]*\)".*/\1/p' <<<"$NUSPEC")

echo
echo "Built $PACKAGE"
echo "  id       $(field id)"
echo "  version  $(field version)"
echo "  authors  $(field authors)"
echo "  commit   ${COMMIT:-(none)}"
echo "  size     $(du -h "$PACKAGE" | cut -f1)"
echo "  sha256   $(sha256sum "$PACKAGE" | cut -d' ' -f1)"
echo
echo "Upload it at https://www.nuget.org/packages/manage/upload"
