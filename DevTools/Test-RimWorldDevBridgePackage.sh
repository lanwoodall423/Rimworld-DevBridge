#!/usr/bin/env bash
set -euo pipefail

archive=${1:?usage: Test-RimWorldDevBridgePackage.sh archive.zip [source-core.dll]}
source_core=${2:-}
tmp=$(mktemp -d "${TMPDIR:-/tmp}/rimworld-devbridge-package.XXXXXX")
trap 'rm -rf "$tmp"' EXIT

if command -v unzip >/dev/null 2>&1; then
unzip_command=(unzip)
unzip_archive="$archive"
unzip_destination="$tmp"
elif command -v unzip.exe >/dev/null 2>&1; then
    unzip_command=(unzip.exe)
    unzip_archive="$archive"
    unzip_destination="$tmp"
    if command -v cygpath >/dev/null 2>&1; then
        unzip_archive=$(cygpath -m "$archive")
        unzip_destination=$(cygpath -m "$tmp")
    elif command -v wslpath >/dev/null 2>&1; then
        unzip_archive=$(wslpath -m "$archive")
        unzip_destination=$(wslpath -m "$tmp")
    fi
else
    echo "unzip is required for portable ZIP verification" >&2
    exit 1
fi

expected=$'About/About.xml\nBRIDGE_MANIFEST.txt\nLoadFolders.xml\n1.6/Assemblies/RimWorldDevBridge.dll\nRestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe'
entries=$("${unzip_command[@]}" -Z1 -- "$archive")

while IFS= read -r entry; do
    [[ -n "$entry" ]] || { echo "empty ZIP entry" >&2; exit 1; }
    [[ "$entry" != *\\* ]] || { echo "backslash ZIP entry: $entry" >&2; exit 1; }
    [[ "$entry" != /* && "$entry" != [A-Za-z]:* && "$entry" != *:* ]] || {
        echo "rooted or drive-qualified ZIP entry: $entry" >&2; exit 1;
    }
    IFS='/' read -r -a parts <<< "$entry"
    for part in "${parts[@]}"; do
        [[ -n "$part" && "$part" != . && "$part" != .. ]] || {
            echo "unsafe ZIP entry segment: $entry" >&2; exit 1;
        }
    done
done <<< "$entries"

duplicates=$(printf '%s\n' "$entries" | LC_ALL=C sort -f | uniq -di)
[[ -z "$duplicates" ]] || { echo "duplicate ZIP entries:"; printf '%s\n' "$duplicates"; exit 1; }

actual=$(printf '%s\n' "$entries" | LC_ALL=C sort)
expected_sorted=$(printf '%s\n' "$expected" | LC_ALL=C sort)
[[ "$actual" == "$expected_sorted" ]] || {
    echo "ZIP entries do not match the declared package manifest" >&2
    diff -u <(printf '%s\n' "$expected_sorted") <(printf '%s\n' "$actual") >&2 || true
    exit 1
}

"${unzip_command[@]}" -q -d "$unzip_destination" "$unzip_archive"
mapfile -t files < <(cd "$tmp" && find . -type f -printf '%P\n' | LC_ALL=C sort)
[[ "${#files[@]}" -eq 5 ]] || { echo "unexpected extracted file count" >&2; exit 1; }

core="$tmp/1.6/Assemblies/RimWorldDevBridge.dll"
[[ -f "$core" ]] || { echo "core DLL is missing after extraction" >&2; exit 1; }
if [[ -n "$source_core" ]]; then
    cmp -- "$source_core" "$core"
    if command -v sha256sum >/dev/null 2>&1; then
        source_hash=$(sha256sum -- "$source_core" | awk '{print toupper($1)}')
        package_hash=$(sha256sum -- "$core" | awk '{print toupper($1)}')
    else
        source_hash=$(shasum -a 256 -- "$source_core" | awk '{print toupper($1)}')
        package_hash=$(shasum -a 256 -- "$core" | awk '{print toupper($1)}')
    fi
    [[ "$source_hash" == "$package_hash" ]] || { echo "core SHA-256 mismatch" >&2; exit 1; }
    echo "coreSha256=$package_hash"
fi

grep -F '<li>1.6</li>' "$tmp/LoadFolders.xml" >/dev/null
grep -F '<packageId>brrainz.harmony</packageId>' "$tmp/About/About.xml" >/dev/null
[[ ! -d "$tmp/DevTools" ]] || { echo "adapter/development directory was extracted" >&2; exit 1; }
[[ -f "$tmp/RestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe" ]] || {
    echo "restart coordinator is missing after extraction" >&2; exit 1;
}
echo "unixPackageVerification=PASS entries=5"
