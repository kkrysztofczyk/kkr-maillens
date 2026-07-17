using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace KKR.MailLens;

/// <summary>Jeden mail zebrany z Outlooka - plaski DTO gotowy do zapisu w korpusie.</summary>
sealed class HarvestedMail
{
    public string EntryId = "", StoreId = "", FolderPath = "", FolderLeaf = "", ConversationId = "";
    public string? Received, Sent;
    public string SenderName = "", SenderEmail = "", Subject = "", Body = "", Categories = "";
    public string ToRecips = "", CcRecips = "", AttachmentNames = "";
    public bool HasAttachments, Unread;
    public int Size;
}

/// <summary>
/// Wlasciciel polaczenia COM z Outlookiem na dedykowanym watku STA (Outlook Object Model jest
/// apartment-affine). Integracja wykonuje wyłącznie operacje odczytu.
/// </summary>
sealed class Outlook : IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _sta;
    readonly TaskCompletionSource<bool> _ready = new();
    dynamic? _app, _ns;
    Exception? _startupError;

    public Outlook()
    {
        _sta = new Thread(Pump) { IsBackground = true, Name = "kkr-maillens-sta" };
        _sta.SetApartmentState(ApartmentState.STA);
        _sta.Start();
    }

    public T Invoke<T>(Func<dynamic, T> withNamespace)
    {
        _ready.Task.GetAwaiter().GetResult();
        if (_startupError != null)
            throw new InvalidOperationException("Nie udalo sie polaczyc z Outlookiem (COM).", _startupError);
        var tcs = new TaskCompletionSource<T>();
        _queue.Add(() =>
        {
            try { tcs.SetResult(withNamespace((object)_ns!)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    void Pump()
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("Outlook.Application");
            if (t == null) throw new InvalidOperationException("Brak ProgID Outlook.Application - Outlook desktop nie zainstalowany.");
            _app = Activator.CreateInstance(t);
            _ns = _app!.GetNamespace("MAPI");
            _ready.TrySetResult(true);
        }
        catch (Exception ex) { _startupError = ex; _ready.TrySetResult(true); return; }

        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try { job(); } catch { }
        }
    }

    public void Dispose() => _queue.CompleteAdding(); // nie Quit() - to instancja uzytkownika

    // ---- zbieranie ----

    /// <summary>Zbiera maile ze WSZYSTKICH folderow pocztowych skrzynek pasujacych do storeContains,
    /// z wyjatkiem Deleted Items / Drafts / Junk / Outbox (+ ich poddrzew). from = dolna granica ReceivedTime.
    /// onFolder = wchodze w folder; onProgress = biezaca liczba zebranych (co ~200, dla dlugiego harvestu).</summary>
    /// <summary>Zapis STRUMIENIOWY: co batchSize zebranych maili wola flush(batch) i czysci bufor -
    /// niski RAM + trwalosc (padniecie gubi tylko ostatnia porcje). Zwraca laczna liczbe maili.</summary>
    public int HarvestMail(string? storeContains, DateTime? from, int maxPerFolder,
        Action<string> onFolder, Action<int, int>? onProgress, Action<List<HarvestedMail>> flush,
        string[]? includeLeaves = null, int batchSize = 500)
    {
        // includeLeaves != null => bierz TYLKO foldery o tych nazwach (reszta pominieta, np. Projekty/Archiwum)
        HashSet<string>? include = null;
        if (includeLeaves != null && includeLeaves.Length > 0)
        {
            include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in includeLeaves) if (!string.IsNullOrWhiteSpace(s)) include.Add(s.Trim());
        }

        return Invoke(ns =>
        {
            var acc = new List<HarvestedMail>();
            int totalMails = 0;

            // 1) zbierz foldery-cele ze wszystkich pasujacych skrzynek
            var targets = new List<(dynamic Folder, string Path, string Leaf, string Sid, string Disp)>();
            dynamic stores = ns.Stores;
            int sn = (int)stores.Count;
            for (int i = 1; i <= sn; i++)
            {
                dynamic store = stores[i];
                string name = Str(() => (string)store.DisplayName);
                if (!string.IsNullOrWhiteSpace(storeContains) && !name.Contains(storeContains!, StringComparison.OrdinalIgnoreCase)) continue;
                string sid = Str(() => (string)store.StoreID);
                var excluded = ExcludedFolderIds(store);
                var fs = new List<dynamic>();
                try { CollectMailFolders((object)store.GetRootFolder(), excluded, fs, true, include); } catch { }
                foreach (dynamic f in fs)
                {
                    string leaf = Str(() => (string)f.Name);
                    targets.Add((f, Str(() => (string)f.FolderPath), leaf, sid, $"{name} / {leaf}"));
                }
            }

            // 2) policz ile elementow planujemy przeskanowac (baza dla procentow). Zwalniamy RCW kolekcji
            //    Items zaraz po odczycie Count - inaczej wyciek jednej kolekcji COM na kazdy folder.
            int total = 0;
            foreach (var t in targets)
            {
                dynamic? items = null;
                try { items = t.Folder.Items; total += Math.Min((int)items.Count, maxPerFolder); }
                catch { }
                finally { if (items != null) Release((object)items); }
            }

            // 3) skanuj; postep (zeskanowane/total) + flush porcjami
            int scanned = 0;
            Action onScanned = () => { scanned++; if (onProgress != null && scanned % 100 == 0) onProgress(scanned, total); };
            Action onAdded = () => { totalMails++; if (acc.Count >= batchSize) { flush(acc); acc.Clear(); } };
            foreach (var t in targets)
            {
                onFolder(t.Disp);
                ScanFolder(t.Folder, t.Path, t.Leaf, t.Sid, from, maxPerFolder, acc, onScanned, onAdded);
            }
            if (acc.Count > 0) flush(acc);
            if (onProgress != null) onProgress(scanned, total);
            return totalMails;
        });
    }

    // EntryID domyslnych folderow do pominiecia (jezykowo-niezalezne): DeletedItems=3, Outbox=4, Drafts=16, Junk=23.
    static HashSet<string> ExcludedFolderIds(dynamic store)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int t in new[] { 3, 4, 16, 23 })
        {
            try { dynamic f = store.GetDefaultFolder(t); string id = (string)f.EntryID; if (!string.IsNullOrEmpty(id)) ids.Add(id); }
            catch { }
        }
        return ids;
    }

    const string PR_ATTR_HIDDEN = "http://schemas.microsoft.com/mapi/proptag/0x10F4000B";

    // Nazwy folderow systemowych/szumu (EN+PL) do pominiecia - blocklista, bo ta skrzynka nie oznacza
    // ich wszystkich jako PR_ATTR_HIDDEN. Pasujaca nazwa ucina folder + poddrzewo (np. Sync Issues/*).
    static readonly HashSet<string> BlockedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sync issues", "conflicts", "local failures", "server failures",
        "rss feeds", "rss subscriptions", "kanaly rss", "kanały rss",
        "quick step settings", "ustawienia szybkich krokow", "ustawienia szybkich kroków",
        "conversation action settings", "quarantine", "kwarantanna", "detected items",
        "outbox", "skrzynka nadawcza", "yammer root", "team chat", "eventcheckpoints",
        "social activity notifications", "files", "feeds", "inbound", "outbound",
        "contacts", "kontakty", "tasks", "zadania", "calendar", "kalendarz",
        "notes", "notatki", "journal", "dziennik", "suggested contacts", "organizational contacts",
    };

    // Foldery pocztowe (DefaultItemType==0) poza: root, wykluczonymi defaultami, systemowymi/ukrytymi
    // i nazwami z blocklisty. Kazde wykluczenie ucina cale poddrzewo.
    static void CollectMailFolders(object folderObj, HashSet<string> excluded, List<dynamic> into, bool isRoot, HashSet<string>? include = null)
    {
        dynamic folder = folderObj;
        string eid = ""; try { eid = (string)folder.EntryID; } catch { }
        if (eid.Length > 0 && excluded.Contains(eid)) return; // Deleted/Drafts/Junk/Outbox + poddrzewo
        string name = Str(() => (string)folder.Name).Trim();
        if (!isRoot && BlockedNames.Contains(name)) return;   // systemowe/szum + poddrzewo
        if (IsHidden(folder)) return;                          // ukryte + poddrzewo
        bool wanted = include == null || include.Contains(name); // include => tylko wskazane nazwy
        if (!isRoot && wanted && Int(() => (int)folder.DefaultItemType, -1) == 0) into.Add(folder);
        try
        {
            dynamic subs = folder.Folders;
            int n = (int)subs.Count;
            for (int i = 1; i <= n; i++) CollectMailFolders((object)subs[i], excluded, into, false, include);
        }
        catch { }
    }

    static bool IsHidden(dynamic folder)
    {
        try { return (bool)folder.PropertyAccessor.GetProperty(PR_ATTR_HIDDEN); }
        catch { return false; }
    }

    static void ScanFolder(dynamic folder, string path, string leaf, string sid, DateTime? from, int maxPerFolder, List<HarvestedMail> acc, Action? onScanned = null, Action? onAdded = null)
    {
        dynamic items;
        try { items = folder.Items; } catch { return; }
        bool sorted = true;
        try { items.Sort("[ReceivedTime]", true); } catch { sorted = false; }
        int scanned = 0;
        dynamic? it = null; try { it = items.GetFirst(); } catch { }
        while (it != null && scanned < maxPerFolder)
        {
            scanned++;
            onScanned?.Invoke(); // licznik postepu: kazdy przejrzany element
            dynamic cur = it!;
            try
            {
                if (IsMail(cur))
                {
                    DateTime? recv = null; try { recv = (DateTime)cur.ReceivedTime; } catch { }
                    if (from is { } f && recv is { } r0 && r0 < f)
                    {
                        if (sorted) { Release(cur); break; }  // desc sort -> koniec
                        it = Next(items, cur); continue;        // brak sortu -> pomin, skanuj dalej
                    }
                    acc.Add(Read(cur, path, leaf, sid));
                    onAdded?.Invoke();
                }
            }
            catch { }
            it = Next(items, cur);
        }
    }

    static HarvestedMail Read(dynamic mail, string path, string leaf, string sid)
    {
        var m = new HarvestedMail
        {
            EntryId = Str(() => (string)mail.EntryID),
            StoreId = sid,
            FolderPath = path,
            FolderLeaf = leaf,
            ConversationId = Str(() => (string)mail.ConversationID),
            Received = DateStr(() => mail.ReceivedTime),
            Sent = DateStr(() => mail.SentOn),
            SenderName = Str(() => (string)mail.SenderName),
            SenderEmail = SenderSmtp(mail),
            Subject = Str(() => (string)mail.Subject),
            Body = Str(() => (string)mail.Body),
            Categories = Str(() => (string)mail.Categories),
            Unread = Bool(() => (bool)mail.UnRead),
            Size = Int(() => (int)mail.Size),
        };
        var to = new List<string>(); var cc = new List<string>();
        try
        {
            dynamic recips = mail.Recipients;
            int rn = (int)recips.Count;
            for (int i = 1; i <= rn; i++)
            {
                dynamic r = recips[i];
                string combo = Combine(Str(() => (string)r.Name), RecipientSmtp(r));
                int type = Int(() => (int)r.Type); // 1=To 2=Cc 3=Bcc
                if (type == 2) cc.Add(combo); else if (type != 3) to.Add(combo);
            }
        }
        catch { }
        m.ToRecips = string.Join("; ", to);
        m.CcRecips = string.Join("; ", cc);
        try
        {
            dynamic atts = mail.Attachments;
            int an = (int)atts.Count;
            m.HasAttachments = an > 0;
            var names = new List<string>();
            for (int i = 1; i <= an; i++) names.Add(Str(() => (string)atts[i].FileName));
            m.AttachmentNames = string.Join("; ", names);
        }
        catch { }
        return m;
    }

    // ---- helpery COM (port z Com.cs / Mapping.cs) ----
    const string PR_SMTP_ENTRY = "http://schemas.microsoft.com/mapi/proptag/0x39FE001E";
    const string PR_SENDER_SMTP = "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";
    const string PR_SENTREP_SMTP = "http://schemas.microsoft.com/mapi/proptag/0x5D02001F";
    const string PR_SMTP_ADDRESS = "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";
    const string PR_TRANSPORT_HEADERS = "http://schemas.microsoft.com/mapi/proptag/0x007D001F";

    static bool IsMail(dynamic item)
    {
        try { string cls = (string)item.MessageClass; return cls != null && cls.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string SenderSmtp(dynamic mail)
    {
        try
        {
            try
            {
                dynamic e = mail.Sender;
                try { string a = (string)e.PropertyAccessor.GetProperty(PR_SMTP_ENTRY); if (HasAt(a)) return a; } catch { }
                try { string a = (string)e.GetExchangeUser().PrimarySmtpAddress; if (HasAt(a)) return a; } catch { }
                try { string a = (string)e.Address; if (HasAt(a)) return a; } catch { }
            }
            catch { }
            try { string a = (string)mail.PropertyAccessor.GetProperty(PR_SENDER_SMTP); if (HasAt(a)) return a; } catch { }
            try { string a = (string)mail.PropertyAccessor.GetProperty(PR_SENTREP_SMTP); if (HasAt(a)) return a; } catch { }
            string addr = Str(() => (string)mail.SenderEmailAddress);
            if (HasAt(addr)) return addr;
            try { string a = Str(() => (string)mail.SendUsingAccount.SmtpAddress); if (HasAt(a)) return a; } catch { }
            string fromHeader = FromHeaderAddress(mail);
            return fromHeader.Length > 0 ? fromHeader : addr;
        }
        catch { return ""; }
    }

    static string FromHeaderAddress(dynamic mail)
    {
        try
        {
            string headers = (string)mail.PropertyAccessor.GetProperty(PR_TRANSPORT_HEADERS);
            var line = Regex.Match(headers ?? "", @"(?im)^(?:From|De)\s*:\s*(.+)$");
            if (!line.Success) return "";
            var addr = Regex.Match(line.Groups[1].Value, @"<([^<>\s]+@[^<>\s]+)>|([^\s<>,;]+@[^\s<>,;]+)");
            return addr.Success ? (addr.Groups[1].Value.Length > 0 ? addr.Groups[1].Value : addr.Groups[2].Value) : "";
        }
        catch { return ""; }
    }

    static string RecipientSmtp(dynamic r)
    {
        try
        {
            try { string a = (string)r.AddressEntry.GetExchangeUser().PrimarySmtpAddress; if (HasAt(a)) return a; } catch { }
            try { string a = (string)r.PropertyAccessor.GetProperty(PR_SMTP_ADDRESS); if (HasAt(a)) return a; } catch { }
            return Str(() => (string)r.Address);
        }
        catch { return ""; }
    }

    static bool HasAt(string a) => !string.IsNullOrEmpty(a) && a.Contains('@');
    static string Combine(string name, string addr) =>
        string.IsNullOrEmpty(addr) ? name : (string.IsNullOrEmpty(name) ? addr : $"{name} <{addr}>");

    static string Str(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    static int Int(Func<int> f, int fallback = 0) { try { return f(); } catch { return fallback; } }
    static bool Bool(Func<bool> f) { try { return f(); } catch { return false; } }
    static string? DateStr(Func<object> f)
    {
        try { var v = f(); return v is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : null; }
        catch { return null; }
    }

    static dynamic? Next(dynamic items, dynamic cur)
    {
        dynamic? next = null;
        try { next = items.GetNext(); } catch { }
        Release(cur);
        return next;
    }

    static void Release(object o) { try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { } }
}
