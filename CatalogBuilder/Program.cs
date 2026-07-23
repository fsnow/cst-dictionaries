// CatalogBuilder — package built dictionary assets into a CST Reader release.
//
//   Input : one or more built dictionary .db files (dpd-cst-subset.db, dppn.db, …)
//   Output: for each, a gzipped <name>.db.gz; and one catalog manifest dictionaries.manifest.json
//           listing every asset with its version axes, SHA-256, and sizes.
//
// CST Reader's DpdUpdateService polls the latest release, reads dictionaries.manifest.json, and for each
// dictionary compares (sourceVersion, converterVersion) to the installed asset — downloading + verifying
// (SHA-256 of the .gz) + installing when newer or absent.
//
// The catalog id + version axes are derived from each db's OWN `meta` table, so adding a dictionary needs
// no change here:
//   - a DPD-lemma db (meta has `dpd_version`)      -> id "dpd",              source = dpd_version
//   - a lexicon db    (meta has `source_id`)       -> id = source_id (dppn), source = source_version
// Both carry `converter_version` (our converter's version — the second axis).
//
// Usage: CatalogBuilder <asset1.db> [<asset2.db> …] [--out <dir>]
//        (--out defaults to the directory of the first asset)

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

const int CatalogSchemaVersion = 1;
const string ManifestName = "dictionaries.manifest.json";

var dbPaths = new List<string>();
string? outDir = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--out" or "-o")
    {
        if (i + 1 >= args.Length) { Console.Error.WriteLine("--out needs a directory"); return 2; }
        outDir = args[++i];
    }
    else dbPaths.Add(args[i]);
}

if (dbPaths.Count == 0)
{
    Console.Error.WriteLine("usage: CatalogBuilder <asset1.db> [<asset2.db> …] [--out <dir>]");
    return 2;
}

outDir ??= Path.GetDirectoryName(Path.GetFullPath(dbPaths[0]))!;
Directory.CreateDirectory(outDir);

var entries = new List<CatalogEntry>();
foreach (var db in dbPaths)
{
    if (!File.Exists(db)) { Console.Error.WriteLine($"missing: {db}"); return 1; }

    var meta = ReadMeta(db);
    var (id, sourceVersion) = DeriveId(meta, db);
    if (id is null)
    {
        Console.Error.WriteLine(
            $"cannot classify {db}: meta has neither `dpd_version` nor `source_id`.");
        return 1;
    }
    if (!meta.TryGetValue("converter_version", out var conv) || conv.Length == 0)
    {
        Console.Error.WriteLine($"{db}: meta has no `converter_version`.");
        return 1;
    }

    var gzName = Path.GetFileName(db) + ".gz";
    var gzPath = Path.Combine(outDir, gzName);
    long rawBytes = new FileInfo(db).Length;
    GzipFile(db, gzPath);
    long compressedBytes = new FileInfo(gzPath).Length;
    string sha = Sha256Hex(gzPath);

    entries.Add(new CatalogEntry(id, gzName, sourceVersion, conv, sha, compressedBytes, rawBytes));
    Console.WriteLine(
        $"  {id,-6} {gzName}  source={sourceVersion} conv={conv}  {compressedBytes / 1024 / 1024} MB gz  sha256={sha[..12]}…");
}

var catalog = new Catalog(CatalogSchemaVersion, entries);
var manifestPath = Path.Combine(outDir, ManifestName);
var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
});
File.WriteAllText(manifestPath, json + Environment.NewLine);

Console.WriteLine($"\nwrote {manifestPath} ({entries.Count} dictionaries)");
Console.WriteLine("\nrelease the manifest + every .gz together, e.g.:");
var assets = string.Join(" ", new[] { ManifestName }.Concat(entries.Select(e => e.File)));
Console.WriteLine($"  (cd {outDir} && gh release create <tag> {assets} --title <title> --notes <notes>)");
return 0;

// ---- helpers ----

static Dictionary<string, string> ReadMeta(string db)
{
    var m = new Dictionary<string, string>(StringComparer.Ordinal);
    var csb = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
    using var conn = new SqliteConnection(csb.ToString());
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT key, value FROM meta";
    using var r = cmd.ExecuteReader();
    while (r.Read())
        if (!r.IsDBNull(1)) m[r.GetString(0)] = r.GetString(1);
    return m;
}

// (catalog id, sourceVersion) from the db's meta. A DPD-lemma db is keyed by dpd_version; a lexicon by
// its source_id + source_version.
static (string? id, string sourceVersion) DeriveId(Dictionary<string, string> meta, string db)
{
    if (meta.TryGetValue("dpd_version", out var dpdVer) && dpdVer.Length > 0)
        return ("dpd", dpdVer);
    if (meta.TryGetValue("source_id", out var sourceId) && sourceId.Length > 0)
        return (sourceId, meta.TryGetValue("source_version", out var sv) ? sv : "");
    return (null, "");
}

static void GzipFile(string src, string dst)
{
    using var input = File.OpenRead(src);
    using var output = File.Create(dst);
    using var gz = new GZipStream(output, CompressionLevel.Optimal);
    input.CopyTo(gz);
}

static string Sha256Hex(string path)
{
    using var fs = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
}

// The catalog manifest CST Reader reads. Property names serialize camelCase (see options above).
record Catalog(int SchemaVersion, List<CatalogEntry> Dictionaries);
record CatalogEntry(
    string Id, string File, string SourceVersion, string ConverterVersion,
    string Sha256, long CompressedBytes, long RawBytes);
