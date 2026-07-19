using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

/// <summary>Odczyt korpusu: FTS5 (subject/body/sender/recips) + filtry strukturalne, oraz statystyki.</summary>
static class Query
{
    public static int Run(string keyHex, string[] args)
    {
        using var c = Db.Open(keyHex, create: false);
        Db.EnsureSchema(c); // auto-migracja (np. dodaje kolumne kind starszym bazom) - brak "no such column"
        return Run(c, args);
    }

    internal static int Run(SqliteConnection c, string[] args)
    {
        // '--' konczy flagi: wszystko po nim to tekst FTS (GUI tak przekazuje frazy zaczynajace sie od '--').
        // Bez separatora: pierwszy nie-flagowy argument po 'query' (moze byc pusty -> czysto strukturalne).
        int sep = Array.IndexOf(args, "--");
        string[] flags = sep >= 0 ? args[..sep] : args;
        string fts = sep >= 0 ? string.Join(' ', args[(sep + 1)..]) : FirstPositional(args);
        string? from = Args.Str(flags, "--from"), to = Args.Str(flags, "--to"), sender = Args.Str(flags, "--sender"), folder = Args.Str(flags, "--folder");
        int limit = Args.Int(flags, "--limit", 25);

        if (!TryDateBound(from, exclusiveUpper: false, out string? fromBound))
        { Console.Error.WriteLine($"Nieprawidlowa data --from: '{from}'. Format: yyyy-MM-dd lub yyyy-MM-dd HH:mm[:ss]."); return 1; }
        if (!TryDateBound(to, exclusiveUpper: true, out string? toBound))
        { Console.Error.WriteLine($"Nieprawidlowa data --to: '{to}'. Format: yyyy-MM-dd lub yyyy-MM-dd HH:mm[:ss]."); return 1; }

        var where = new List<string>();
        var ps = new List<(string, object)>();
        bool useFts = !string.IsNullOrWhiteSpace(fts);
        bool raw = Args.Flag(flags, "--raw"); // --raw = przekaz zapytanie wprost (skladnia FTS: AND/OR/prefix*)
        if (useFts) { where.Add("mails_fts MATCH $q"); ps.Add(("$q", raw ? fts : FtsSanitize(fts))); }
        if (fromBound != null) { where.Add("m.received >= $from"); ps.Add(("$from", fromBound)); }
        if (toBound != null) { where.Add("m.received < $to"); ps.Add(("$to", toBound)); }
        if (!string.IsNullOrWhiteSpace(sender)) { where.Add(@"(m.sender_name LIKE $snd ESCAPE '\' OR m.sender_email LIKE $snd ESCAPE '\')"); ps.Add(("$snd", "%" + EscapeLike(sender!) + "%")); }
        if (!string.IsNullOrWhiteSpace(folder)) { where.Add(@"m.folder_leaf LIKE $fld ESCAPE '\'"); ps.Add(("$fld", EscapeLike(folder!) + "%")); } // dopasuj realny leaf (inbox/sent/archiwum/...), nie zgaduj

        // Domyslnie odsiewamy alerty (firehose). --all = wszystko, --alerts = tylko alerty.
        bool all = Args.Flag(flags, "--all"), alertsOnly = Args.Flag(flags, "--alerts");
        if (alertsOnly) where.Add("m.kind = 'alert'");
        else if (!all) where.Add("(m.kind IS NULL OR m.kind <> 'alert')");

        string join = useFts ? "JOIN mails_fts f ON f.rowid = m.rowid" : "";
        string sql = $"""
            SELECT m.received, m.folder_leaf, m.sender_name, m.sender_email, m.subject,
                   substr(replace(replace(m.body, char(13), ' '), char(10), ' '), 1, 160) AS snip
            FROM mails m {join}
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY {(useFts ? "bm25(mails_fts)" : "m.received DESC")}
            LIMIT $lim;
            """;

        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; cmd.Parameters.Add(p); }
        var pl = cmd.CreateParameter(); pl.ParameterName = "$lim"; pl.Value = limit; cmd.Parameters.Add(pl);

        int n0 = 0;
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                n0++;
                string recv = r.IsDBNull(0) ? "?" : r.GetString(0);
                string leaf = r.IsDBNull(1) ? "" : r.GetString(1);
                string snd = r.IsDBNull(2) ? "" : r.GetString(2);
                string em = r.IsDBNull(3) ? "" : r.GetString(3);
                string subj = r.IsDBNull(4) ? "" : r.GetString(4);
                string snip = r.IsDBNull(5) ? "" : r.GetString(5).Trim();
                string tag = leaf.StartsWith("Sent", StringComparison.OrdinalIgnoreCase) ? "->" : "<-";
                Console.WriteLine($"{recv}  {tag} {(snd.Length > 0 ? snd : em)}");
                Console.WriteLine($"    {subj}");
                if (snip.Length > 0) Console.WriteLine($"    │ {snip}");
            }
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Blad zapytania: {ex.Message}");
            Console.Error.WriteLine("Podpowiedz: uzyj prostszych slow; dla skladni FTS (AND/OR/prefix*) dodaj --raw.");
            return 1;
        }
        string alertNote = alertsOnly ? " [tylko alerty]" : (all ? " [z alertami]" : " [alerty ukryte - --all/--alerts by wlaczyc]");
        Console.WriteLine((n0 == 0 ? "(brak trafien)" : $"-- {n0} trafien" + (n0 >= limit ? " (limit - zawez lub podnies --limit)" : "")) + alertNote);
        return 0;
    }

    public static int Stats(string keyHex)
    {
        using var c = Db.Open(keyHex, create: false);
        Db.EnsureSchema(c); // auto-migracja (np. dodaje kolumne kind starszym bazom) - brak "no such column"
        long total = Corpus.Count(c);
        long alert = Convert.ToInt64(Scalar(c, "SELECT count(*) FROM mails WHERE kind='alert';") ?? "0");
        Console.WriteLine($"Maili w korpusie: {total}  (korespondencja: {total - alert}, alerty: {alert})");
        Console.WriteLine($"Ostatni harvest: {Meta(c, "last_harvest") ?? "(brak)"}");
        Console.WriteLine($"Zakres dat: {Scalar(c, "SELECT min(received) FROM mails;") ?? "?"}  ..  {Scalar(c, "SELECT max(received) FROM mails;") ?? "?"}");

        Console.WriteLine("\nPer folder:");
        Each(c, "SELECT folder_leaf, count(*) FROM mails GROUP BY folder_leaf ORDER BY 2 DESC;",
            r => Console.WriteLine($"  {r.GetString(0),-14} {r.GetInt64(1)}"));

        Console.WriteLine("\nTop 10 nadawcow:");
        Each(c, "SELECT COALESCE(NULLIF(sender_name,''), sender_email) s, count(*) FROM mails GROUP BY s ORDER BY 2 DESC LIMIT 10;",
            r => Console.WriteLine($"  {r.GetInt64(1),4}  {r.GetString(0)}"));

        long att = Convert.ToInt64(Scalar(c, "SELECT count(*) FROM mails WHERE has_attachments=1;") ?? "0");
        Console.WriteLine($"\nZ zalacznikami: {att}");
        return 0;
    }

    // Flagi z wartoscia (konsumuja nastepny argument). Pozostale '--*' NIE konsumuja - tak samo jak
    // ContentSearch.FirstPositional i Cli.QueryText; nieznana flaga nie moze "zjesc" np. --limit.
    static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase) { "--from", "--to", "--sender", "--folder", "--limit" };

    // Kolumna received przechowuje UTC "yyyy-MM-dd HH:mm:ss"; granice porownujemy leksykograficznie.
    // Gorna granica jest wylaczna: dla samej daty liczymy poczatek nastepnego dnia (caly dzien wlacznie).
    internal static bool TryDateBound(string? input, bool exclusiveUpper, out string? bound)
    {
        bound = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        string s = input.Trim();
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        { bound = (exclusiveUpper ? d.AddDays(1) : d).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); return true; }
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
        { bound = d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); return true; }
        return false;
    }

    // % i _ z wejscia uzytkownika to literaly, nie wildcards LIKE ('_' jest czesty w adresach e-mail).
    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // Bezpieczne zapytanie FTS5: kazdy token w cudzyslow -> znaki specjalne (. - @ : itp.) traktowane
    // jako literaly, brak "syntax error". Wiele tokenow = implicit AND. Wewnetrzne " podwojone.
    static string FtsSanitize(string q)
    {
        var sb = new StringBuilder();
        foreach (var t in q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(t.Replace("\"", "\"\"")).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>Analiza wolumenu -> podpowiada kandydatow na szum (deterministycznie: liczby + %,
    /// oznacza co juz jest w regulach [SZUM] i flaguje duzych nie-objetych). Decyzja usera.</summary>
    public static int Analyze(string keyHex, int top)
    {
        var rules = NoiseRules.Load();
        using var c = Db.Open(keyHex, create: false);
        Db.EnsureSchema(c); // auto-migracja (np. dodaje kolumne kind starszym bazom) - brak "no such column"
        long total = Corpus.Count(c);
        long alert = Convert.ToInt64(Scalar(c, "SELECT count(*) FROM mails WHERE kind='alert';") ?? "0");
        Console.WriteLine($"Korpus: {total}  (korespondencja {total - alert}, alerty {alert})");

        Console.WriteLine($"\nTop {top} nadawcow (wolumen):");
        Each(c, $"SELECT sender_email, COALESCE(NULLIF(sender_name,''), sender_email) nm, count(*) c FROM mails GROUP BY sender_email ORDER BY c DESC LIMIT {top};",
            r =>
            {
                string email = r.IsDBNull(0) ? "" : r.GetString(0);
                string nm = r.IsDBNull(1) ? "" : r.GetString(1);
                long cnt = r.GetInt64(2);
                double pct = total > 0 ? 100.0 * cnt / total : 0;
                bool noise = rules.IsNoise("", email, nm);
                string tag = noise ? "[SZUM]" : (pct >= 3 ? "<- kandydat na szum?" : "");
                Console.WriteLine($"  {cnt,6}  {pct,3:0}%  {(email.Length > 0 ? email : nm),-46} {tag}");
            });

        Console.WriteLine("\nFoldery (wolumen):");
        Each(c, "SELECT folder_leaf, count(*) c FROM mails GROUP BY folder_leaf ORDER BY c DESC;",
            r =>
            {
                string fl = r.IsDBNull(0) ? "" : r.GetString(0);
                long cnt = r.GetInt64(1);
                Console.WriteLine($"  {cnt,6}  {fl,-22} {(rules.IsNoise(fl, "", "") ? "[SZUM]" : "")}");
            });

        Console.WriteLine($"\nReguly (edytuj recznie, potem 'reclassify'): {Paths.NoiseRulesFile}");
        Console.WriteLine("Kandydata dorzuc do noiseSenders (dokladny adres) / noiseFolders (nazwa) / noiseSenderContains (fragment).");
        return 0;
    }

    /// <summary>Wklad kazdej reguly: brutto (ile lapie) + unikat (ile TYLKO ta regula - odpadloby po jej usunieciu)
    /// + zazebienie (maile trafione przez >1 regule). Deterministycznie, na danych korpusu.</summary>
    public static int AnalyzeRules(string keyHex)
    {
        var rules = NoiseRules.Load();
        using var c = Db.Open(keyHex, create: false);
        Db.EnsureSchema(c); // auto-migracja (np. dodaje kolumne kind starszym bazom) - brak "no such column"

        var gross = new Dictionary<string, int>();
        var unique = new Dictionary<string, int>();
        long total = 0, alerts = 0, overlap = 0;

        using (var sel = c.CreateCommand())
        {
            sel.CommandText = "SELECT folder_leaf, sender_email, sender_name FROM mails;";
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                total++;
                var hits = rules.MatchingRules(
                    r.IsDBNull(0) ? "" : r.GetString(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2));
                if (hits.Count == 0) continue;
                alerts++;
                if (hits.Count > 1) overlap++;
                foreach (var h in hits) gross[h] = gross.GetValueOrDefault(h) + 1;
                if (hits.Count == 1) unique[hits[0]] = unique.GetValueOrDefault(hits[0]) + 1;
            }
        }

        Console.WriteLine($"Maili: {total} | alerty (>=1 regula): {alerts} | trafione przez >1 regule (zazebienie): {overlap}");
        Console.WriteLine("\nWklad regul:  brutto = ile lapie,  unikat = ile TYLKO ta (odpadloby po usunieciu)");
        Console.WriteLine($"  {"regula",-45} {"brutto",8} {"unikat",8}");
        foreach (var kv in gross.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {kv.Key,-45} {kv.Value,8} {unique.GetValueOrDefault(kv.Key),8}");
        Console.WriteLine("\nunikat=0 => regula w pelni pokryta przez inne (kandydat do usuniecia).");
        return 0;
    }

    static string FirstPositional(string[] args)
    {
        // args[0] = "query"; szukamy pierwszego argumentu ktory nie jest flaga ani wartoscia flagi
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { if (ValueFlags.Contains(args[i])) i++; continue; }
            return args[i];
        }
        return "";
    }

    static string? Meta(SqliteConnection c, string k)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k=$k;";
        var p = cmd.CreateParameter(); p.ParameterName = "$k"; p.Value = k; cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() as string;
    }

    static object? Scalar(SqliteConnection c, string sql)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; var v = cmd.ExecuteScalar(); return v is DBNull ? null : v; }

    static void Each(SqliteConnection c, string sql, Action<SqliteDataReader> row)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; using var r = cmd.ExecuteReader(); while (r.Read()) row(r); }
}

/// <summary>JEDNO zrodlo prawdy parsowania argumentow CLI. Ten plik jest linkowany do obu assembly
/// (CLI i GUI), a Cli.cs tylko do CLI - dlatego helpery mieszkaja tu, a Cli deleguje do nich.</summary>
static class Args
{
    public static string? Str(string[] args, string name)
    { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    public static int Int(string[] args, string name, int fallback)
        => int.TryParse(Str(args, name), out var v) ? v : fallback;
    public static bool Flag(string[] args, string name)
        => Array.IndexOf(args, name) >= 0;
}
