#!/usr/bin/env bash
# Packs SqlServerSimulator into a local folder and checks that its opt-in
# buildTransitive targets file does what the README says, through four throwaway
# consumers' runtimeconfig.json:
#
#   a  direct reference, SqlServerSimulatorDisableTieredPgo=true   -> TieredPGO false
#   b  direct reference, no switch                                 -> no TieredPGO key
#   c  switch on and an explicit TieredPGO=true                    -> TieredPGO true
#   d  switch on, package reached only through a class library     -> TieredPGO false
#
# Also asserts the .nupkg carries buildTransitive/SqlServerSimulator.targets and
# nothing under build/, that each restore imports the targets exactly once, that
# `dotnet msbuild -getProperty:TieredPGO` agrees, and that consumer a reports
# AppContext "System.Runtime.TieredPGO" as false when it runs.
#
# Nothing is published: the package goes to a folder under $WORK (default: a new
# temp directory) and the consumers restore from it. Run it whenever the targets
# file or its packing lines change; it isn't part of `dotnet test`.
#
#   ./tools/package-check/check.sh [work-dir]
set -uo pipefail
export MSBUILDDISABLENODEREUSE=1
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${1:-$(mktemp -d)}"
mkdir -p "$WORK"
# MSBuild and NuGet want a native path (C:/...), not Git Bash's /c/....
if command -v cygpath >/dev/null 2>&1; then WORK="$(cygpath -m "$WORK")"; fi
FEED="$WORK/feed"
# A packages folder of its own, so a package restored by an earlier run is never reused.
export NUGET_PACKAGES="$WORK/packages"
VERSION="0.0.0-packagecheck"
mkdir -p "$FEED"
failed=0
fail() { echo "FAIL: $*" >&2; failed=1; }
ok() { echo "ok:   $*"; }

echo "work directory: $WORK"
dotnet pack "$REPO/src/SqlServerSimulator/SqlServerSimulator.csproj" -c Release -o "$FEED" -p:Version=$VERSION -nologo -v q || { echo "pack failed" >&2; exit 1; }
NUPKG="$FEED/SqlServerSimulator.$VERSION.nupkg"
[ -f "$NUPKG" ] || { echo "no $NUPKG" >&2; exit 1; }

entries="$(unzip -Z1 "$NUPKG" 2>/dev/null || python -c "import sys,zipfile;print('
'.join(zipfile.ZipFile(sys.argv[1]).namelist()))" "$NUPKG")"
echo "$entries" | grep -qx 'buildTransitive/SqlServerSimulator.targets' && ok "nupkg lists buildTransitive/SqlServerSimulator.targets" || fail "nupkg lacks buildTransitive/SqlServerSimulator.targets"
echo "$entries" | grep -q '^build/' && fail "nupkg has a build/ folder (double import risk)" || ok "nupkg has no build/ folder"

cat > "$WORK/nuget.config" <<NC
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="SqlServerSimulator" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
NC
# Keep the repo's own props (warnings, CPM, analyzers) out of the consumers.
echo '<Project />' > "$WORK/Directory.Build.props"
echo '<Project />' > "$WORK/Directory.Build.targets"
echo '<Project />' > "$WORK/Directory.Packages.props"
cp "$REPO/global.json" "$WORK/global.json" 2>/dev/null || true

PROGRAM_PRINT='System.Console.Write("TieredPGO=" + System.AppContext.GetData("System.Runtime.TieredPGO"));'

consumer() { # name, extra properties, reference style (direct|library)
    local name="$1" props="$2" style="$3" dir="$WORK/$1"
    mkdir -p "$dir"
    if [ "$style" = direct ]; then
        cat > "$dir/$name.csproj" <<P
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    $props
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SqlServerSimulator" Version="$VERSION" />
  </ItemGroup>
</Project>
P
    else
        mkdir -p "$WORK/$name-lib"
        cat > "$WORK/$name-lib/lib.csproj" <<P
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SqlServerSimulator" Version="$VERSION" />
  </ItemGroup>
</Project>
P
        echo 'namespace Lib; public static class Marker { public static object Make() => new SqlServerSimulator.Simulation(); }' > "$WORK/$name-lib/Marker.cs"
        cat > "$dir/$name.csproj" <<P
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    $props
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../$name-lib/lib.csproj" />
  </ItemGroup>
</Project>
P
    fi
    if [ "$style" = direct ]; then
        echo "_ = new SqlServerSimulator.Simulation(); $PROGRAM_PRINT" > "$dir/Program.cs"
    else
        echo "_ = Lib.Marker.Make(); $PROGRAM_PRINT" > "$dir/Program.cs"
    fi
    # Per-consumer nuget.config location is the shared one above this folder.
    ( cd "$dir" && dotnet build "$name.csproj" -c Release -warnaserror -nologo -v q ) || { fail "$name: build"; return; }
    local rc="$dir/bin/Release/net10.0/$name.runtimeconfig.json"
    [ -f "$rc" ] || { fail "$name: no runtimeconfig.json"; return; }
    local imports; imports="$(grep -c '<Import Project=.*buildTransitive.SqlServerSimulator\.targets"' "$dir/obj/$name.csproj.nuget.g.targets" 2>/dev/null)"
    [ "$imports" = 1 ] && ok "$name: restore imports the targets once" || fail "$name: restore imports the targets $imports times"
}

expect() { # name, expected runtimeconfig value (false|true|absent), expected msbuild property ('' for unset)
    local name="$1" want="$2" prop="$3"
    local rc="$WORK/$name/bin/Release/net10.0/$name.runtimeconfig.json"
    [ -f "$rc" ] || return
    local got; got="$(grep -o '"System.Runtime.TieredPGO": *[a-z]*' "$rc" | sed 's/.*: *//')"
    [ -z "$got" ] && got=absent
    [ "$got" = "$want" ] && ok "$name: runtimeconfig TieredPGO = $got" || fail "$name: runtimeconfig TieredPGO = $got, wanted $want"
    local p; p="$(cd "$WORK/$name" && dotnet msbuild "$name.csproj" -getProperty:TieredPGO -nologo | tr -d '\r\n ')"
    [ "$p" = "$prop" ] && ok "$name: msbuild TieredPGO = '$p'" || fail "$name: msbuild TieredPGO = '$p', wanted '$prop'"
}

consumer a '<SqlServerSimulatorDisableTieredPgo>true</SqlServerSimulatorDisableTieredPgo>' direct
consumer b '' direct
consumer c '<SqlServerSimulatorDisableTieredPgo>true</SqlServerSimulatorDisableTieredPgo><TieredPGO>true</TieredPGO>' direct
consumer d '<SqlServerSimulatorDisableTieredPgo>true</SqlServerSimulatorDisableTieredPgo>' library

expect a false false
expect b absent ''
expect c true true
expect d false false

if [ -f "$WORK/a/bin/Release/net10.0/a.dll" ]; then
    out="$(dotnet "$WORK/a/bin/Release/net10.0/a.dll")"
    [ "$out" = "TieredPGO=false" ] && ok "a: AppContext reports $out" || fail "a: AppContext reported '$out', wanted TieredPGO=false"
fi

[ "$failed" = 0 ] && echo "package check passed" || { echo "package check FAILED" >&2; exit 1; }
