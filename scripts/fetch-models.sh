#!/usr/bin/env bash
# Puts the beat and key models in RPM.Desktop/Assets/OnnxModels without building the app.
#
# A build of RPM.Desktop does this by itself: the FetchRpmModels target in RPM.Desktop.csproj
# downloads each missing model from https://updates.rpmrpmrpm.com/models/<sha256>/<name> and checks
# its hash. That csproj holds the list and the hashes; this script only runs the target. It is for
# the tools that read Assets/OnnxModels without building RPM.Desktop: TempoBench, LyricBench and
# scripts/quantize_beat_this.py.
#
# No AWS profile is needed. The models are public and come from the update host.
#
#   scripts/fetch-models.sh
#
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
dotnet msbuild RPM.Desktop/RPM.Desktop.csproj -t:FetchRpmModels -nologo -verbosity:minimal

echo
echo "Models are in RPM.Desktop/Assets/OnnxModels."
echo "For the eight-bit beat model, which is what an x86 machine wants:"
echo "  python3 scripts/quantize_beat_this.py"
