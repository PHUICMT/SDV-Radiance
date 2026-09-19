#!/bin/sh
# Everything a commit on main must still pass, in one command, so the state of 2026-09-15
# (no Thai in code, Debug and Release at 0 warnings, no style hint left, the harness green)
# cannot drift back one warning at a time.
#
#     sh tools/build-check.sh
#
# Exits non-zero on the first failure and says which step. Never deploys: the game can stay open.
# Needs the game installed where the project finds it, because the mod builds against its DLLs.

repository_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repository_root" || exit 1
build_flags="-p:EnableModDeploy=false -p:EnableModZip=false --nologo -v q -warnaserror"

step() {
    echo ""
    echo "== $1"
}

fail() {
    echo ""
    echo "build-check FAILED at: $1"
    exit 1
}

step "no Thai in code"
python tools/thaisweep.py src shaders tools || fail "Thai sweep"

step "one range per setting (Clamp, menu, tuner)"
python tools/check-setting-ranges.py || fail "setting ranges"

step "Debug build, warnings are errors"
dotnet build ./SDV-Radiance.csproj -c Debug $build_flags || fail "Debug build"

step "Release build, warnings are errors"
dotnet build ./SDV-Radiance.csproj -c Release $build_flags || fail "Release build"

step "no style hint left (.editorconfig)"
dotnet format style ./SDV-Radiance.csproj --verify-no-changes --severity info -v q || fail "dotnet format"

step "label pack harness"
dotnet build tests/LabelPackHarness/LabelPackHarness.csproj $build_flags || fail "harness build"
(cd tests/LabelPackHarness && dotnet run --no-build) || fail "harness run"

echo ""
echo "build-check passed"
