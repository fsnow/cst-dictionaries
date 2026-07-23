# cst-dictionaries

Derived dictionary **data assets** for [**CST Reader**](https://github.com/fsnow/cst) (the cross-platform
Pāli Tipiṭaka reader), together with the local build tools that produce them. This repo is CST Reader's
dictionary **distribution point**: the app downloads assets from this repo's Releases and reads them; the app
itself lives at [github.com/fsnow/cst](https://github.com/fsnow/cst).

## What's here

Each dictionary is a **separate asset** with its **own provenance and license** — nothing is merged into one
file, and a published **manifest** carries the per-dictionary metadata (id, display name, version, checksum,
attribution, and license *where one applies*). Today:

| id | asset | what it is | source | license |
|----|-------|-----------|--------|---------|
| `dpd` | `dpd-cst-subset.db` | a trimmed, corpus-agnostic subset of the Digital Pāḷi Dictionary — form→lemma resolution, lemma/report metadata, sandhi deconstructions | [Digital Pāḷi Dictionary](https://www.dpdict.net/) (Bhikkhu Bodhirāsa) | CC BY-NC-SA 4.0 (see `LICENSE`) |
| `dppn` | `dppn.db` | the Dictionary of Pāli Proper Names — people, places, texts, as an entity reference | G. P. Malalasekera, rev. Ānandajoti Bhikkhu (2025), [Ancient Buddhist Texts](https://ancient-buddhist-texts.net/Textual-Studies/DPPN/index.htm) | — |

More dictionaries are added the same way: a build tool, a released asset, a manifest entry — no CST Reader
release required.

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

## Releases

Assets are built **locally** (a scheduled task on a dev machine, not GitHub CI) and attached to GitHub
Releases as `<asset>.db.zst` + `.sha256`, alongside the manifest. A new release is cut when an upstream source
publishes a new version **or** a build tool's converter/schema version bumps. CST Reader's update service polls
this repo, compares the manifest, and downloads what changed.

## Attribution & licensing

**Per dictionary — the manifest is authoritative; there is no single blanket license over every asset.**

- **DPD** (`dpd-cst-subset.db`) is a derivative of the Digital Pāḷi Dictionary and is licensed **CC BY-NC-SA
  4.0** (the `LICENSE` file), per DPD's ShareAlike terms. The `DpdLemmaBuilder` tool is covered the same way.
  Upstream DPD attribution + version travel in the asset's `meta` table. CST Reader is non-commercial.
- **DPPN** (`dppn.db`) is G. P. Malalasekera's Dictionary of Pāli Proper Names as revised by Ānandajoti
  Bhikkhu (2025); that attribution travels in the asset's `meta`.

The `LICENSE` file in this repo applies to the DPD-derived material and tooling; other assets carry their own
provenance in their `meta` and in the manifest.
