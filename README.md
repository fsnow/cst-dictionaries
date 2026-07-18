# dpd-cst-subset

<!-- One-line description: "the DPD subset for CST" — a derived subset of DPD for CST Reader (not a re-brand). -->
_TODO_

## What the asset contains

<!-- dpd-cst-subset.db built from a DPD dpd.db release: form→lemma + lemma metadata, report-grade columns +
     root table, sandhi deconstructions (full scope). No occurrence counts (those come from CST Reader's index).
     Forms stored in IAST. -->
_TODO_

## The generator tool

<!-- DpdLemmaBuilder/ — self-contained .NET console app (Microsoft.Data.Sqlite only). -->

```bash
dotnet run -c Release --project DpdLemmaBuilder -- <dpd.db> <out/dpd-cst-subset.db> --scope full
```

<!-- Scopes: lean | mid | full. Validation pass + version tripwire (pinned to the current DPD release). -->
_TODO_

## Releases

<!-- Built locally (scheduled task, not CI); dpd-cst-subset.db.zst + .sha256 + manifest attached as a GitHub
     Release, tagged by DPD version + scope. New release when: (1) DPD publishes a new dpd.db, OR (2) our
     converter/schema version bumps. Consumed by CST Reader's DpdUpdateService. -->
_TODO_

## Attribution & licence

<!-- Derived from the Digital Pāḷi Dictionary (Bodhirāsa, https://www.dpdict.net/) under CC BY-NC-SA 4.0.
     Per ShareAlike, this repo (tool + released asset) is CC BY-NC-SA 4.0 (see LICENSE). CST Reader is
     non-commercial. Upstream DPD attribution + version travel in the asset's meta table. -->
_TODO_
