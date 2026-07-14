#!/usr/bin/env bash
set -euo pipefail

script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_root/../.." && pwd)"
gallery_root="$repo_root/Sample Applications/WPFGallery"
configuration="${CONFIGURATION:-Release}"
gallery_output="${1:-$gallery_root/bin/$configuration/net10.0-windows}"
hook_output="$script_root/bin/$configuration/net10.0/LibreWpfGalleryAcceptance.dll"
log_path="${LIBREWPF_GALLERY_ACCEPTANCE_LOG:-/tmp/librewpf-wpfgallery-acceptance.log}"

if [[ ! -x "$gallery_output/WPFGallery" ]]; then
  echo "WPFGallery apphost was not found at: $gallery_output/WPFGallery" >&2
  echo "Build WPFGallery in $configuration configuration before running this gate." >&2
  exit 2
fi

dotnet build "$script_root/LibreWpfGalleryAcceptance.csproj" \
  -c "$configuration" \
  -p:GalleryOutput="$gallery_output" \
  --nologo

rm -f "$log_path"
(
  cd "$gallery_output"
  env \
    DOTNET_STARTUP_HOOKS="$hook_output" \
    LIBREWPF_GALLERY_ACCEPTANCE_LOG="$log_path" \
    ./WPFGallery
)

grep -F "PASS:" "$log_path"
