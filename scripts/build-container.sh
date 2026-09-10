#!/usr/bin/env bash
set -euo pipefail

# Build against the exact Emby runtime ABI without installing .NET on the host.
# EMBY_IMAGE can be overridden for another target version.
emby_image=${EMBY_IMAGE:-emby/embyserver@sha256:3aafff933d3f28d23ed0bc201022abe71c0aa80deb17177566c726b9bbc686c6}
sdk_image=${DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}
repo_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
ref_dir="$repo_dir/libs"
artifact_dir="$repo_dir/artifacts"
container_name="infinitedrive-refs-$$"

mkdir -p "$ref_dir" "$artifact_dir"
cleanup() { docker rm -f "$container_name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker create --name "$container_name" "$emby_image" >/dev/null
for assembly in \
  MediaBrowser.Controller.dll MediaBrowser.Model.dll MediaBrowser.Common.dll \
  Emby.Web.GenericEdit.dll Emby.Web.GenericUI.dll Emby.Media.Model.dll \
  SQLitePCL.pretty.dll SQLitePCLRawEx.core.dll; do
  docker cp "$container_name:/system/$assembly" "$ref_dir/$assembly"
done
cleanup

docker run --rm -v "$repo_dir:/src" -w /src "$sdk_image" \
  sh -c 'dotnet restore Tests/InfiniteDrive.Tests.csproj --force --nologo && dotnet test Tests/InfiniteDrive.Tests.csproj -c Release --no-restore --nologo'
docker run --rm -v "$repo_dir:/src" -w /src "$sdk_image" \
  dotnet publish InfiniteDrive.csproj -c Release -t:Rebuild -o /src/artifacts --nologo

sha256sum "$artifact_dir/InfiniteDrive.dll"
