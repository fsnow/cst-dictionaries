#!/usr/bin/env bash
# =============================================================================================
# build-release.sh — build the CST Reader dictionary assets from upstream and publish a release.
#
# Produces the catalog manifest (dictionaries.manifest.json) + one gzipped .db per dictionary,
# and publishes them as a GitHub Release that CST Reader's DpdUpdateService polls. Designed to
# run unattended on a schedule (launchd on Egret), but safe to run by hand.
#
# Per-dictionary change detection (only rebuild/republish what actually moved):
#   - dpd  : the DPD subset. Source = digitalpalidictionary/dpd-db releases/latest (dpd.db, 169 MB).
#            EXPENSIVE, so we rebuild ONLY when that release tag differs from the installed asset's
#            dpd_version (or the cached db is missing, or --force). Built with DpdLemmaBuilder --scope full.
#   - dppn : Proper Names. Source = digitalpalidictionary/other-dictionaries dictionaries/dppn/dppn.tar.zst.
#            CHEAP (~2 MB), so we ALWAYS rebuild it and compare the output's (source_version, converter_version)
#            to what's in the current release. source_version = other-dictionaries releases/latest tag.
#
# A new release is cut only if some dictionary's (source_version, converter_version) changed vs the current
# release — unless --force. Unchanged assets are still re-attached (the release must be self-contained so a
# fresh install resolves every dictionary from the single latest release).
#
# Flags:
#   --force        rebuild + republish everything, ignore change detection and the cadence gate
#   --no-gate      ignore the minimum-days-between-releases cadence gate
#   --dry-run      build + report what WOULD be released; touch no GitHub release
#   --delete-latest  delete the current latest release before publishing (a true from-scratch cut)
#
# Requires: gh (authenticated), dotnet, git, tar, zstd, bzip2, sqlite3, python3.
# =============================================================================================
set -euo pipefail

# ---- config ----
RELEASE_REPO="fsnow/cst-dictionaries"
DPD_REPO="digitalpalidictionary/dpd-db"
OD_REPO="digitalpalidictionary/other-dictionaries"
DPPN_ARCHIVE_PATH="dictionaries/dppn/dppn.tar.zst"
MIN_DAYS_BETWEEN_RELEASES=7

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="${CST_DICT_WORK:-$HOME/.cache/cst-dictionaries}"   # persistent build cache (survives runs)
CACHE="$WORK/cache"                                       # built .db files kept between runs
REL="$WORK/release"                                       # staged release payload (rebuilt each run)
mkdir -p "$CACHE" "$REL"

FORCE=0; NO_GATE=0; DRY_RUN=0; DELETE_LATEST=0
for a in "$@"; do case "$a" in
  --force) FORCE=1; NO_GATE=1 ;;
  --no-gate) NO_GATE=1 ;;
  --dry-run) DRY_RUN=1 ;;
  --delete-latest) DELETE_LATEST=1 ;;
  *) echo "unknown flag: $a" >&2; exit 2 ;;
esac; done

log() { printf '%s  %s\n' "$(date '+%H:%M:%S')" "$*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

# ---- 0. cadence gate --------------------------------------------------------------------------
LATEST_JSON="$(gh api "repos/$RELEASE_REPO/releases/latest" 2>/dev/null || echo '{}')"
LATEST_TAG="$(echo "$LATEST_JSON" | python3 -c 'import sys,json;print(json.load(sys.stdin).get("tag_name",""))')"
if [ "$NO_GATE" -eq 0 ] && [ -n "$LATEST_TAG" ]; then
  CREATED="$(echo "$LATEST_JSON" | python3 -c 'import sys,json;print(json.load(sys.stdin).get("created_at",""))')"
  AGE_DAYS="$(python3 -c "import datetime,sys;d=datetime.datetime.fromisoformat('$CREATED'.replace('Z','+00:00'));print((datetime.datetime.now(datetime.timezone.utc)-d).days)" 2>/dev/null || echo 999)"
  if [ "$AGE_DAYS" -lt "$MIN_DAYS_BETWEEN_RELEASES" ]; then
    log "last release ($LATEST_TAG) is ${AGE_DAYS}d old (< ${MIN_DAYS_BETWEEN_RELEASES}d gate) — nothing to do."
    exit 0
  fi
fi

# The current release's per-dictionary versions (to compare against), from its catalog.
CUR_CATALOG="$(gh release download "$LATEST_TAG" --repo "$RELEASE_REPO" -p dictionaries.manifest.json -O - 2>/dev/null || echo '{}')"
cur_ver() { echo "$CUR_CATALOG" | python3 -c "import sys,json
d=json.load(sys.stdin)
e=next((x for x in d.get('dictionaries',[]) if x['id']=='$1'),None)
print(f\"{e['sourceVersion']}|{e['converterVersion']}\" if e else '')"; }

# ---- 1. DPD (expensive; change-detected against its release tag) --------------------------------
DPD_DB="$CACHE/dpd-cst-subset.db"
DPD_TAG="$(gh api "repos/$DPD_REPO/releases/latest" --jq .tag_name)"
[ -n "$DPD_TAG" ] || die "could not read $DPD_REPO latest tag"

# Seed the cache from a db already in the working tree, if present and matching (avoids a needless 169 MB pull).
if [ ! -f "$DPD_DB" ] && [ -f "$REPO_DIR/dpd-cst-subset.db" ]; then
  seeded="$(sqlite3 "$REPO_DIR/dpd-cst-subset.db" "SELECT value FROM meta WHERE key='dpd_version'" 2>/dev/null || echo)"
  [ "$seeded" = "$DPD_TAG" ] && { log "seeding DPD cache from repo db ($seeded)"; cp "$REPO_DIR/dpd-cst-subset.db" "$DPD_DB"; }
fi
DPD_HAVE="$( [ -f "$DPD_DB" ] && sqlite3 "$DPD_DB" "SELECT value FROM meta WHERE key='dpd_version'" 2>/dev/null || echo )"

if [ "$FORCE" -eq 1 ] || [ "$DPD_HAVE" != "$DPD_TAG" ]; then
  log "DPD: rebuilding (have='${DPD_HAVE:-none}', latest=$DPD_TAG) — downloading dpd.db (~169 MB)…"
  tmp="$WORK/dpd-src"; rm -rf "$tmp"; mkdir -p "$tmp"
  gh release download "$DPD_TAG" --repo "$DPD_REPO" -p 'dpd.db.tar.bz2' -O "$tmp/dpd.db.tar.bz2"
  tar -xjf "$tmp/dpd.db.tar.bz2" -C "$tmp"
  DPD_SRC="$(find "$tmp" -name dpd.db -maxdepth 3 | head -1)"; [ -n "$DPD_SRC" ] || die "dpd.db not found in archive"
  dotnet run -c Release --project "$REPO_DIR/DpdLemmaBuilder" -- "$DPD_SRC" "$DPD_DB" --scope full
  rm -rf "$tmp"
else
  log "DPD: unchanged ($DPD_HAVE) — reusing cached build."
fi

# ---- 2. DPPN (cheap; always rebuilt) -----------------------------------------------------------
DPPN_DB="$CACHE/dppn.db"
DPPN_TAG="$(gh api "repos/$OD_REPO/releases/latest" --jq .tag_name)"   # Bodhirāsa's shelf version = our source_version
[ -n "$DPPN_TAG" ] || die "could not read $OD_REPO latest tag"
log "DPPN: fetching source ($DPPN_ARCHIVE_PATH) + building (source_version=$DPPN_TAG)…"
tmp="$WORK/dppn-src"; rm -rf "$tmp"; mkdir -p "$tmp"
gh api "repos/$OD_REPO/contents/$DPPN_ARCHIVE_PATH?ref=main" --jq '.download_url' \
  | xargs curl -sSL -o "$tmp/dppn.tar.zst"
tar --zstd -xf "$tmp/dppn.tar.zst" -C "$tmp"
DPPN_JSON="$(find "$tmp" -name 'DPPN.json' | head -1)"; [ -n "$DPPN_JSON" ] || die "DPPN.json not found in archive"
dotnet run -c Release --project "$REPO_DIR/DppnBuilder" -- "$DPPN_JSON" "$DPPN_DB" "$DPPN_TAG"
rm -rf "$tmp"

# ---- 3. package the catalog (all assets — the release must be self-contained) -------------------
rm -f "$REL"/*.db "$REL"/*.db.gz "$REL"/dictionaries.manifest.json 2>/dev/null || true
cp "$DPD_DB" "$REL/dpd-cst-subset.db"; cp "$DPPN_DB" "$REL/dppn.db"
dotnet run -c Release --project "$REPO_DIR/CatalogBuilder" -- \
  "$REL/dpd-cst-subset.db" "$REL/dppn.db" --out "$REL"
rm -f "$REL"/*.db   # keep only the .gz + manifest for upload

# ---- 4. decide whether anything changed --------------------------------------------------------
new_ver() { python3 -c "import json;d=json.load(open('$REL/dictionaries.manifest.json'));e=next(x for x in d['dictionaries'] if x['id']=='$1');print(f\"{e['sourceVersion']}|{e['converterVersion']}\")"; }
CHANGED=0
for id in dpd dppn; do
  [ "$(new_ver "$id")" != "$(cur_ver "$id")" ] && { log "  $id changed: '$(cur_ver "$id")' -> '$(new_ver "$id")'"; CHANGED=1; }
done
if [ "$CHANGED" -eq 0 ] && [ "$FORCE" -eq 0 ]; then
  log "no dictionary changed vs $LATEST_TAG — not releasing."
  exit 0
fi

# ---- 5. publish --------------------------------------------------------------------------------
TAG="dictionaries-$(date +%Y-%m-%d)"
TITLE="Dictionaries $(date +%Y-%m-%d)"
ASSETS=("$REL/dictionaries.manifest.json" "$REL"/*.db.gz)
if [ "$DRY_RUN" -eq 1 ]; then
  log "DRY RUN — would publish $TAG with: $(printf '%s ' "${ASSETS[@]##*/}")"
  cat "$REL/dictionaries.manifest.json"; exit 0
fi

[ "$DELETE_LATEST" -eq 1 ] && [ -n "$LATEST_TAG" ] && { log "deleting current release $LATEST_TAG"; gh release delete "$LATEST_TAG" --repo "$RELEASE_REPO" --cleanup-tag --yes || true; }

if gh release view "$TAG" --repo "$RELEASE_REPO" >/dev/null 2>&1; then
  log "updating existing release $TAG (clobbering assets)"
  gh release upload "$TAG" "${ASSETS[@]}" --repo "$RELEASE_REPO" --clobber
else
  log "creating release $TAG"
  gh release create "$TAG" "${ASSETS[@]}" --repo "$RELEASE_REPO" \
    --title "$TITLE" --notes "Automated dictionary release. See the repo README for per-dictionary provenance and licensing."
fi
log "done — https://github.com/$RELEASE_REPO/releases/tag/$TAG"
