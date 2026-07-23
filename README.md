# cst-dictionaries

Derived dictionary **data assets** for [**CST Reader**](https://github.com/fsnow/cst) (the cross-platform
Pāli Tipiṭaka reader), together with the local build tools that produce them. This repo is CST Reader's
dictionary **distribution point**: the app downloads assets from this repo's Releases and reads them; the app
itself lives at [github.com/fsnow/cst](https://github.com/fsnow/cst).

## What's here

Each dictionary is a **separate asset** with its **own provenance and license** — nothing is merged into one
file, and a single published **catalog manifest** (`dictionaries.manifest.json`) lists every asset with its
version axes, checksum, and sizes. Attribution + license *where one applies* travel in each asset's own `meta`
table. Today:

| id | asset | what it is | source | license |
|----|-------|-----------|--------|---------|
| `dpd` | `dpd-cst-subset.db` | a trimmed, corpus-agnostic subset of the Digital Pāḷi Dictionary — form→lemma resolution, lemma/report metadata, sandhi deconstructions | [Digital Pāḷi Dictionary](https://www.dpdict.net/) (Bhikkhu Bodhirāsa) | CC BY-NC-SA 4.0 (see `LICENSE`) |
| `dppn` | `dppn.db` | the Dictionary of Pāli Proper Names — people, places, texts, as an entity reference | G. P. Malalasekera, rev. Ānandajoti Bhikkhu (2025), [Ancient Buddhist Texts](https://ancient-buddhist-texts.net/Textual-Studies/DPPN/index.htm) | — |

A **new version** of a listed dictionary ships with no CST Reader release: cut a release, and the app picks it
up. A **brand-new dictionary** additionally needs the app to learn its descriptor (install path + version
reader + usability probe) once — after that, its versions flow the same way.

## Build tools

Each tool is a **standalone .NET console app** (only `Microsoft.Data.Sqlite`, no CST Reader dependency), run
**locally** to produce an asset. They are deliberately not part of any solution and not run in CI.

### `DpdLemmaBuilder/` → `dpd-cst-subset.db`

Builds the DPD subset from a full DPD `dpd.db` release.

```bash
dotnet run -c Release --project DpdLemmaBuilder -- <dpd.db> <out/dpd-cst-subset.db> --scope full
```

Scopes: `lean` (resolver only) | `mid` (+ per-form grammar) | `full` (+ sandhi deconstructions). Forms are
stored in IAST; CST Reader converts to IPE at query time.

### `DppnBuilder/` → `dppn.db`

Converts the DPPN source `DPPN.json` into a **lexicon** — the canonical CST Reader dictionary format: a small
SQLite of `meta(key,value)` + `entry(id, headword, body_html)`. No lookup key is stored; CST Reader derives
the IPE key and homonym from the headword at read time, so this tool only extracts the headword (the lemma is
the first `<b>` run in DPPN's formatted-heading `name`, with a trailing homonym number/range) and reduces the
definition body to a closed HTML tag allowlist.

```bash
dotnet run -c Release --project DppnBuilder -- <DPPN.json> <out/dppn.db> <sourceVersion>
```

### `CatalogBuilder/` → `dictionaries.manifest.json` + `<asset>.db.gz`

Packages one or more built asset `.db` files into a release: gzips each, computes its SHA-256 + sizes, reads
its version axes, and writes the single catalog manifest CST Reader polls. The catalog id + versions are read
from each db's own `meta` (a DPD-lemma db by `dpd_version` → id `dpd`; a lexicon by `source_id` +
`source_version`), so it needs no per-dictionary configuration.

```bash
dotnet run -c Release --project CatalogBuilder -- <dpd-cst-subset.db> <dppn.db> --out <release-dir>
```

## Releases

Assets are built **locally** (a scheduled task on a dev machine, not GitHub CI). A release attaches the catalog
manifest `dictionaries.manifest.json` **plus every `<asset>.db.gz`** it references (gzip — CST Reader
decompresses gzip and verifies the SHA-256 of the `.gz`). A new release is cut when an upstream source
publishes a new version **or** a build tool's converter/schema version bumps. CST Reader's update service polls
this repo, reads the catalog, and downloads only the dictionaries whose `(sourceVersion, converterVersion)`
changed — each independently, preserving any existing asset until its replacement is verified. Example:

```bash
cd <release-dir>
gh release create <tag> dictionaries.manifest.json dpd-cst-subset.db.gz dppn.db.gz \
  --title "<title>" --notes "<notes>"
```

## Attribution & licensing

**Per dictionary — the manifest is authoritative; there is no single blanket license over every asset.**

- **DPD** (`dpd-cst-subset.db`) is a derivative of the Digital Pāḷi Dictionary and is licensed **CC BY-NC-SA
  4.0** (the `LICENSE` file), per DPD's ShareAlike terms. The `DpdLemmaBuilder` tool is covered the same way.
  Upstream DPD attribution + version travel in the asset's `meta` table. CST Reader is non-commercial.
- **DPPN** (`dppn.db`) is G. P. Malalasekera's Dictionary of Pāli Proper Names as revised by Ānandajoti
  Bhikkhu (2025); that attribution travels in the asset's `meta`.

The `LICENSE` file in this repo applies to the DPD-derived material and tooling; other assets carry their own
provenance in their `meta` and in the manifest.
