#!/usr/bin/env bash
#
# Pack-output release-gate assertion.
#
# Runs `dotnet pack` against the package csproj, lists the resulting .nupkg's
# entries, and asserts every entry matches an explicit allow-list pattern. Any
# unexpected entry — particularly `content/` or `contentFiles/` items, source
# maps, or other build-time artefacts that shouldn't ship to adopters — fails
# the build with a diagnostic message.
#
# Usage:
#   .github/scripts/assert-pack-output.sh
#
# Exit codes:
#   0 — pack output matches allow-list
#   1 — unexpected entries present, OR pack itself failed
#   2 — usage / environment error
#
# When this script fails, ONE of two things is happening:
#   (a) Your story added a legitimate new entry to the .nupkg → add a row to
#       ALLOWED_PATTERNS below in the same diff. The allow-list is intentional;
#       it forces every new ship surface to be reviewed at story-locking time.
#   (b) An MSBuild/Razor-SDK leak is shipping something it shouldn't (the
#       canonical case the gate exists for). Tighten the csproj ItemGroup or
#       Vite config to suppress the leak rather than allow-listing it.
#
# Do NOT remove or relax this gate without explicit story authorisation.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CSPROJ="${REPO_ROOT}/Umbraco.Community.AiVisibility/Umbraco.Community.AiVisibility.csproj"
PACK_OUT_DIR="${PACK_OUT_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/aiv-pack-assert.XXXXXX")}"

if [[ ! -f "${CSPROJ}" ]]; then
  echo "::error::assert-pack-output: csproj not found at ${CSPROJ}" >&2
  exit 2
fi

# Allow-list of expected paths inside the .nupkg. Each entry is a POSIX
# extended regex (used by `grep -Eq`). Keep entries anchored (`^…$`) so a
# partial match doesn't accidentally allow a leak under a longer path.
#
# Vite output filenames carry an 8-char content-hash suffix (e.g.
# `aiv-ai-traffic-dashboard.element-DkAKzgPI.js`); the regex ranges below
# match that shape. If a Vite hash collision ever extends the suffix length,
# update the bracket count rather than relaxing the anchor.
ALLOWED_PATTERNS=(
  '^_rels/\.rels$'
  '^Umbraco\.Community\.AiVisibility\.nuspec$'
  '^lib/net10\.0/Umbraco\.Community\.AiVisibility\.dll$'
  '^staticwebassets/App_Plugins/UmbracoCommunityAiVisibility/aiv-ai-traffic-dashboard\.element-[A-Za-z0-9_-]+\.js$'
  '^staticwebassets/App_Plugins/UmbracoCommunityAiVisibility/aiv-settings-dashboard\.element-[A-Za-z0-9_-]+\.js$'
  '^staticwebassets/App_Plugins/UmbracoCommunityAiVisibility/authenticated-fetch-[A-Za-z0-9_-]+\.js$'
  '^staticwebassets/App_Plugins/UmbracoCommunityAiVisibility/entrypoint-[A-Za-z0-9_-]+\.js$'
  '^staticwebassets/App_Plugins/UmbracoCommunityAiVisibility/umbraco-community-aivisibility\.js$'
  '^staticwebassets/App_Plugins/UmbracoCommunityAiVisibility/umbraco-package\.json$'
  '^staticwebassets/umbraco-community-aivisibility\.css$'
  '^build/Microsoft\.AspNetCore\.StaticWebAssetEndpoints\.props$'
  '^build/Microsoft\.AspNetCore\.StaticWebAssets\.props$'
  '^build/Umbraco\.Community\.AiVisibility\.props$'
  '^buildMultiTargeting/Umbraco\.Community\.AiVisibility\.props$'
  '^buildTransitive/Umbraco\.Community\.AiVisibility\.props$'
  '^README\.md$'
  '^icon\.png$'
  '^\[Content_Types\]\.xml$'
  '^package/services/metadata/core-properties/[a-f0-9]+\.psmdcp$'
)

echo "==> Running dotnet pack against ${CSPROJ}"
dotnet pack "${CSPROJ}" -c Release -o "${PACK_OUT_DIR}" --nologo

# Exclude `.symbols.nupkg` — when `<IncludeSymbols>` is enabled, `dotnet pack`
# emits both `<v>.nupkg` and `<v>.symbols.nupkg`; `find` order is not
# lexicographic, so a bare `head -n 1` could pick the symbols variant and
# assert against the wrong artefact.
NUPKG_MATCHES="$(find "${PACK_OUT_DIR}" -maxdepth 1 -type f \
  -name 'Umbraco.Community.AiVisibility.*.nupkg' \
  ! -name '*.symbols.nupkg')"
NUPKG_COUNT="$(echo "${NUPKG_MATCHES}" | grep -c '.' || true)"
if [[ "${NUPKG_COUNT}" -eq 0 ]]; then
  echo "::error::assert-pack-output: dotnet pack produced no release .nupkg in ${PACK_OUT_DIR}" >&2
  exit 1
fi
if [[ "${NUPKG_COUNT}" -gt 1 ]]; then
  echo "::error::assert-pack-output: ${NUPKG_COUNT} candidate .nupkg files in ${PACK_OUT_DIR} — expected exactly one. Override PACK_OUT_DIR to a clean directory or clear stale packs." >&2
  echo "${NUPKG_MATCHES}" | sed 's/^/  - /' >&2
  exit 1
fi
NUPKG="${NUPKG_MATCHES}"

echo "==> Inspecting ${NUPKG}"
# `unzip -Z1` lists one path per line, no header, no trailing summary.
ENTRIES="$(unzip -Z1 "${NUPKG}")"

UNEXPECTED=()
while IFS= read -r entry; do
  [[ -z "${entry}" ]] && continue
  matched=0
  for pattern in "${ALLOWED_PATTERNS[@]}"; do
    if echo "${entry}" | grep -Eq "${pattern}"; then
      matched=1
      break
    fi
  done
  if [[ "${matched}" -eq 0 ]]; then
    UNEXPECTED+=("${entry}")
  fi
done <<< "${ENTRIES}"

if [[ "${#UNEXPECTED[@]}" -gt 0 ]]; then
  echo "::error::assert-pack-output: ${#UNEXPECTED[@]} unexpected entry/entries in .nupkg" >&2
  for entry in "${UNEXPECTED[@]}"; do
    echo "  - ${entry}" >&2
  done
  echo "" >&2
  echo "Either add a new ALLOWED_PATTERNS entry (legitimate new ship surface) or" >&2
  echo "tighten the csproj/Vite config (accidental leak). See script header." >&2
  exit 1
fi

echo "==> Pack output matches allow-list ($(echo "${ENTRIES}" | wc -l | tr -d ' ') entries)"
exit 0
