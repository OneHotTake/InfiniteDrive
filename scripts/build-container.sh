#!/usr/bin/env bash
set -euo pipefail

# Test and publish only; never starts or modifies an Emby service.
emby_image=${EMBY_IMAGE:-emby/embyserver@sha256:3aafff933d3f28d23ed0bc201022abe71c0aa80deb17177566c726b9bbc686c6}
sdk_image=${DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}
repo_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
ref_dir="$repo_dir/libs"
artifact_dir="$repo_dir/artifacts"
container_name="infinitedrive-refs-$$"

mkdir -p "$ref_dir" "$artifact_dir"
cleanup() { docker rm -f "$container_name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker create --platform linux/amd64 --name "$container_name" "$emby_image" >/dev/null
for assembly in \
  MediaBrowser.Controller.dll MediaBrowser.Model.dll MediaBrowser.Common.dll \
  Emby.Web.GenericEdit.dll Emby.Web.GenericUI.dll Emby.Media.Model.dll \
  SQLitePCL.pretty.dll SQLitePCLRawEx.core.dll EmbyServer.dll \
  Emby.Server.Implementations.dll Emby.Sqlite.dll; do
  docker cp "$container_name:/system/$assembly" "$ref_dir/$assembly"
done
cleanup

build_image=$(docker build --platform linux/amd64 -q \
  --build-arg "DOTNET_SDK_IMAGE=$sdk_image" "$repo_dir/tools/build")
docker run --rm --platform linux/amd64 \
  --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -v "$repo_dir:/src" -w /src "$build_image" sh -eu -c '
    dotnet restore Tests/InfiniteDrive.Tests.csproj --nologo
    dotnet test Tests/InfiniteDrive.Tests.csproj -c Release --no-restore --nologo
    dotnet publish InfiniteDrive.csproj -c Release --no-restore -o /src/artifacts --nologo
  '

if command -v sha256sum >/dev/null 2>&1; then
  sha256sum "$artifact_dir/InfiniteDrive.dll"
else
  shasum -a 256 "$artifact_dir/InfiniteDrive.dll"
fi
