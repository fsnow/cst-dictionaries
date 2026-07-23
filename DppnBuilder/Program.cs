using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// =====================================================================================================
// DPPN builder — Dictionary of Pāli Proper Names (Malalasekera, rev. Ānandajoti 2025) → CST lexicon.
//
//   Input  : DPPN.json — a JSON array of { "name", "entry" }. `name` is a formatted HEADING (the lemma in
//            the first <b>, an optional bare homonym number or range after it, citations in <abbr>, all in
//            MALFORMED markup); `entry` is the definition HTML.
//   Output : a lexicon SQLite — meta(key,value) + entry(id, headword, body_html). No lookup key is stored:
//            CST Reader derives the IPE key + homonym from the headword at read time.
//
// The body is reduced to a closed tag allowlist (p br i b em strong ul ol li hr abbr), dropping every other
// tag and ALL attributes; stray angle brackets in text are escaped, so the output is provably within the
// allowlist (a build-time post-condition aborts otherwise). Trusted, fixed, public input — a
// normalize-to-our-tag-set filter, not an XSS sanitizer.
//
// Usage: DppnBuilder <DPPN.json> <out.db> [sourceVersion]
// =====================================================================================================

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DppnBuilder <DPPN.json> <out.db> [sourceVersion]");
    return 2;
}
string jsonPath = args[0], dbPath = args[1], sourceVersion = args.Length >= 3 ? args[2] : "unknown";

try
{
    int n = Dppn.Build(jsonPath, dbPath, sourceVersion);
    Console.WriteLine($"DPPN → {dbPath}: {n} entries (source {sourceVersion}).");
    return 0;
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
{
    Console.Error.WriteLine($"DPPN conversion failed: {ex.Message}");
    return 1;
}

static class Dppn
{
    const int SchemaVersion = 1;
    // v2: display_name is now the full title ("Dictionary of Pāli Proper Names") instead of "DPPN", for the
    // source picker. Bumped so the new meta propagates to already-installed copies (the app compares this axis).
    const int ConverterVersion = 2;

    public static int Build(string jsonPath, string dbPath, string sourceVersion)
    {
        string json = File.ReadAllText(jsonPath);
        var rows = ToEntries(json).ToList();

        string tmp = dbPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        // Pooling=false: open-once/close-once, and a pooled handle would resurrect a deleted inode on rebuild
        // and block the atomic rename. (mirrors CST.Lexicon.LexiconBuilder)
        var csb = new SqliteConnectionStringBuilder
        { DataSource = tmp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };

        int written = 0;
        using (var conn = new SqliteConnection(csb.ToString()))
        {
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
            Exec(conn, "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);"
                     + "CREATE TABLE entry(id INTEGER PRIMARY KEY, headword TEXT NOT NULL, body_html TEXT NOT NULL);");

            using var tx = conn.BeginTransaction();
            WriteMeta(conn, sourceVersion);

            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = "INSERT INTO entry(headword, body_html) VALUES ($h, $b)";
                var ph = ins.CreateParameter(); ph.ParameterName = "$h"; ins.Parameters.Add(ph);
                var pb = ins.CreateParameter(); pb.ParameterName = "$b"; ins.Parameters.Add(pb);
                foreach (var (headword, body) in rows)
                {
                    ph.Value = headword; pb.Value = body;
                    ins.ExecuteNonQuery();
                    written++;
                }
            }
            tx.Commit();
        }

        File.Move(tmp, dbPath, overwrite: true);
        return written;
    }

    static void WriteMeta(SqliteConnection conn, string sourceVersion)
    {
        // Attribution but NO license assertion (see the README on per-dictionary licensing).
        var meta = new (string Key, string? Value)[]
        {
            ("schema_version", SchemaVersion.ToString()),
            ("source_id", "dppn"),
            ("display_name", "Dictionary of Pāli Proper Names"),
            ("definition_language", "en"),
            ("kind", "proper-names"),
            ("title", "Dictionary of Pāli Proper Names"),
            ("author", "G. P. Malalasekera"),
            ("reviser", "Ānandajoti Bhikkhu"),
            ("year", "2025"),
            ("url", "https://ancient-buddhist-texts.net/Textual-Studies/DPPN/index.htm"),
            ("source_version", sourceVersion),
            ("converter_version", ConverterVersion.ToString()),
        };
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ($k, $v)";
        var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; cmd.Parameters.Add(pk);
        var pv = cmd.CreateParameter(); pv.ParameterName = "$v"; cmd.Parameters.Add(pv);
        foreach (var (k, v) in meta)
        {
            if (v is null) continue;
            pk.Value = k; pv.Value = v; cmd.ExecuteNonQuery();
        }
    }

    static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static IEnumerable<(string Headword, string Body)> ToEntries(string dppnJson)
    {
        using var doc = JsonDocument.Parse(dppnJson);
        foreach (var rec in doc.RootElement.EnumerateArray())
        {
            string name = rec.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
            string entry = rec.TryGetProperty("entry", out var ee) ? ee.GetString() ?? "" : "";

            string headword = ParseHeadword(name);
            if (headword.Length == 0) continue;

            string body = CleanBody(entry);
            if (WebUtility.HtmlDecode(AnyTag.Replace(body, "")).Trim().Length == 0) continue;   // header stub
            if (!IsClosedAllowlist(body))
                throw new InvalidOperationException($"non-allowlisted markup for '{headword}': {body}");

            yield return (headword, body);
        }
    }

    // ---- headword ----

    static readonly Regex FirstBold = new("<b>(.*?)</b>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex HomonymAfterBold = new(@"</b>\s*0*(\d+)(?:-0*(\d+))?", RegexOptions.Compiled);
    static readonly Regex AnyTag = new("<[^>]*>", RegexOptions.Compiled);

    public static string ParseHeadword(string nameHtml)
    {
        if (string.IsNullOrEmpty(nameHtml)) return "";
        var m = FirstBold.Match(nameHtml);
        if (!m.Success) return "";
        string lemma = WebUtility.HtmlDecode(AnyTag.Replace(m.Value, "")).Trim();
        if (lemma.Length == 0) return "";

        // A homonym number/range after the lemma's </b> (search from the first </b>: the only multi-bold+number
        // headings are "X or Y NN" alternative titles, where the trailing number is the real homonym).
        string tail = nameHtml[(m.Index + m.Length)..];
        var h = HomonymAfterBold.Match("</b>" + tail);
        if (!h.Success || !int.TryParse(h.Groups[1].Value, out int n) || n <= 0) return lemma;
        return h.Groups[2].Success && int.TryParse(h.Groups[2].Value, out int m2)
            ? $"{lemma} {n}-{m2}"   // a range keeps both numbers; read-time split takes the first as the sort key
            : $"{lemma} {n}";
    }

    // ---- body allowlist filter ----

    static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    { "p", "br", "i", "b", "em", "strong", "ul", "ol", "li", "hr", "abbr" };

    // No "\s*" after "<": per the HTML spec "<" + whitespace is text, not a tag-open.
    static readonly Regex TagToken = new("<(/?)([a-zA-Z0-9]+)[^>]*?(/?)>", RegexOptions.Compiled);
    static readonly Regex AllowedBareTag =
        new("</?(?:p|br|i|b|em|strong|ul|ol|li|hr|abbr)/?>", RegexOptions.Compiled);

    public static string CleanBody(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sb = new StringBuilder(html.Length);
        int pos = 0;
        foreach (Match t in TagToken.Matches(html))
        {
            AppendEscaped(sb, html, pos, t.Index);
            pos = t.Index + t.Length;
            string name = t.Groups[2].Value;
            if (!AllowedTags.Contains(name)) continue;
            bool closing = t.Groups[1].Value == "/";
            bool selfClose = t.Groups[3].Value == "/"
                             || name.Equals("br", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("hr", StringComparison.OrdinalIgnoreCase);
            sb.Append(closing ? $"</{name.ToLowerInvariant()}>"
                    : selfClose ? $"<{name.ToLowerInvariant()}/>"
                    : $"<{name.ToLowerInvariant()}>");
        }
        AppendEscaped(sb, html, pos, html.Length);
        return sb.ToString().Trim();
    }

    // Escape stray '<'/'>' in a text run so text can never form or complete a tag; existing entities (&lt; …)
    // carry no '<'/'>' char and pass through.
    static void AppendEscaped(StringBuilder sb, string html, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            char ch = html[i];
            sb.Append(ch == '<' ? "&lt;" : ch == '>' ? "&gt;" : ch.ToString());
        }
    }

    public static bool IsClosedAllowlist(string body)
    {
        string stripped = AllowedBareTag.Replace(body, "");
        return !stripped.Contains('<') && !stripped.Contains('>');
    }
}
