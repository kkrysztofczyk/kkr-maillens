using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

/// <summary>Zapis zebranych maili do zaszyfrowanego korpusu. Upsert po stabilnej tożsamości źródłowej
/// z zachowaniem dotychczasowego entry_id i rowid. FTS synchronizowane ręcznie (delete+insert po rowid).</summary>
static class Corpus
{
    public sealed record Stats(int Inserted, int Updated);

    public static Stats Upsert(SqliteConnection c, IEnumerable<HarvestedMail> mails, string harvestedAt)
    {
        using var transaction = c.BeginTransaction();
        Stats result = Upsert(c, transaction, mails, harvestedAt);
        transaction.Commit();
        return result;
    }

    internal static Stats Upsert(SqliteConnection c, SqliteTransaction transaction,
        IEnumerable<HarvestedMail> mails, string harvestedAt)
    {
        int ins = 0, upd = 0;

        using var exists = c.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT rowid FROM mails WHERE entry_id=$id;";
        var pExId = exists.CreateParameter(); pExId.ParameterName = "$id"; exists.Parameters.Add(pExId);

        using var bySource = c.CreateCommand();
        bySource.Transaction = transaction;
        bySource.CommandText = "SELECT rowid,entry_id FROM mails WHERE source_identity=$source;";
        var pSourceLookup = bySource.CreateParameter(); pSourceLookup.ParameterName = "$source";
        bySource.Parameters.Add(pSourceLookup);

        using var legacy = c.CreateCommand();
        legacy.Transaction = transaction;
        legacy.CommandText = """
            SELECT rowid,entry_id FROM mails
            WHERE source_identity IS NULL AND entry_id=$legacy AND store_id=$store
                AND (folder_path=$folder OR $provider='outlook')
            LIMIT 1;
            """;
        var pLegacyId = legacy.CreateParameter(); pLegacyId.ParameterName = "$legacy"; legacy.Parameters.Add(pLegacyId);
        var pLegacyStore = legacy.CreateParameter(); pLegacyStore.ParameterName = "$store"; legacy.Parameters.Add(pLegacyStore);
        var pLegacyFolder = legacy.CreateParameter(); pLegacyFolder.ParameterName = "$folder"; legacy.Parameters.Add(pLegacyFolder);
        var pLegacyProvider = legacy.CreateParameter(); pLegacyProvider.ParameterName = "$provider";
        legacy.Parameters.Add(pLegacyProvider);

        using var up = c.CreateCommand();
        up.Transaction = transaction;
        up.CommandText = """
            INSERT INTO mails(entry_id,source_identity,store_id,folder_path,folder_leaf,conversation_id,received,sent,
                sender_name,sender_email,to_recips,cc_recips,subject,body,has_attachments,attachment_names,
                size,unread,categories,kind,harvested_at)
            VALUES($entry_id,$source_identity,$store_id,$folder_path,$folder_leaf,$conversation_id,$received,$sent,
                $sender_name,$sender_email,$to_recips,$cc_recips,$subject,$body,$has_attachments,$attachment_names,
                $size,$unread,$categories,$kind,$harvested_at)
            ON CONFLICT(entry_id) DO UPDATE SET
                source_identity=COALESCE(excluded.source_identity,mails.source_identity),
                store_id=excluded.store_id, folder_path=excluded.folder_path, folder_leaf=excluded.folder_leaf,
                conversation_id=excluded.conversation_id, received=excluded.received, sent=excluded.sent,
                sender_name=excluded.sender_name, sender_email=excluded.sender_email,
                to_recips=excluded.to_recips, cc_recips=excluded.cc_recips,
                subject=excluded.subject, body=excluded.body, has_attachments=excluded.has_attachments,
                attachment_names=excluded.attachment_names, size=excluded.size, unread=excluded.unread,
                categories=excluded.categories, kind=excluded.kind, harvested_at=excluded.harvested_at;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "entry_id","source_identity","store_id","folder_path","folder_leaf","conversation_id","received","sent",
            "sender_name","sender_email","to_recips","cc_recips","subject","body","has_attachments","attachment_names",
            "size","unread","categories","kind","harvested_at" })
        { var pp = up.CreateParameter(); pp.ParameterName = "$" + n; up.Parameters.Add(pp); p[n] = pp; }

        using var ftsDel = c.CreateCommand();
        ftsDel.Transaction = transaction;
        ftsDel.CommandText = "DELETE FROM mails_fts WHERE rowid=$rid;";
        var pDelRid = ftsDel.CreateParameter(); pDelRid.ParameterName = "$rid"; ftsDel.Parameters.Add(pDelRid);

        using var ftsIns = c.CreateCommand();
        ftsIns.Transaction = transaction;
        ftsIns.CommandText = "INSERT INTO mails_fts(rowid,subject,body,sender,recips) VALUES($rid,$subject,$body,$sender,$recips);";
        var fRid = ftsIns.CreateParameter(); fRid.ParameterName = "$rid"; ftsIns.Parameters.Add(fRid);
        var fSub = ftsIns.CreateParameter(); fSub.ParameterName = "$subject"; ftsIns.Parameters.Add(fSub);
        var fBody = ftsIns.CreateParameter(); fBody.ParameterName = "$body"; ftsIns.Parameters.Add(fBody);
        var fSend = ftsIns.CreateParameter(); fSend.ParameterName = "$sender"; ftsIns.Parameters.Add(fSend);
        var fRec = ftsIns.CreateParameter(); fRec.ParameterName = "$recips"; ftsIns.Parameters.Add(fRec);

        // rowid dla nowego wiersza bez powtornego SELECT WHERE entry_id (b-tree probe) - INSERT wlasnie go nadal.
        using var lastId = c.CreateCommand();
        lastId.Transaction = transaction;
        lastId.CommandText = "SELECT last_insert_rowid();";

        foreach (var m in mails)
        {
            if (string.IsNullOrEmpty(m.EntryId)) continue;
            string storageEntryId = m.EntryId;
            object? existingRid = null;
            if (!string.IsNullOrWhiteSpace(m.SourceIdentity))
            {
                pSourceLookup.Value = m.SourceIdentity;
                using var sourceReader = bySource.ExecuteReader();
                if (sourceReader.Read())
                {
                    existingRid = sourceReader.GetInt64(0);
                    storageEntryId = sourceReader.GetString(1);
                }
            }
            if (existingRid is null && !string.IsNullOrWhiteSpace(m.LegacyEntryId))
            {
                pLegacyId.Value = m.LegacyEntryId;
                pLegacyStore.Value = m.StoreId;
                pLegacyFolder.Value = m.FolderPath;
                pLegacyProvider.Value = m.AttachmentProvider.Trim().ToLowerInvariant();
                using var legacyReader = legacy.ExecuteReader();
                if (legacyReader.Read())
                {
                    existingRid = legacyReader.GetInt64(0);
                    storageEntryId = legacyReader.GetString(1);
                }
            }
            if (existingRid is null)
            {
                pExId.Value = storageEntryId;
                existingRid = exists.ExecuteScalar();
            }
            bool isNew = existingRid is null or DBNull;

            p["entry_id"].Value = storageEntryId;
            p["source_identity"].Value = string.IsNullOrWhiteSpace(m.SourceIdentity)
                ? DBNull.Value : m.SourceIdentity;
            p["store_id"].Value = m.StoreId;
            p["folder_path"].Value = m.FolderPath;
            p["folder_leaf"].Value = m.FolderLeaf;
            p["conversation_id"].Value = (object?)m.ConversationId ?? DBNull.Value;
            p["received"].Value = (object?)m.Received ?? DBNull.Value;
            p["sent"].Value = (object?)m.Sent ?? DBNull.Value;
            p["sender_name"].Value = m.SenderName;
            p["sender_email"].Value = m.SenderEmail;
            p["to_recips"].Value = m.ToRecips;
            p["cc_recips"].Value = m.CcRecips;
            p["subject"].Value = m.Subject;
            p["body"].Value = m.Body;
            p["has_attachments"].Value = m.HasAttachments ? 1 : 0;
            p["attachment_names"].Value = m.AttachmentNames;
            p["size"].Value = m.Size;
            p["unread"].Value = m.Unread ? 1 : 0;
            p["categories"].Value = m.Categories;
            p["kind"].Value = Classify.Kind(m);
            p["harvested_at"].Value = harvestedAt;
            up.ExecuteNonQuery();

            // existing: rowid zachowany przez ON CONFLICT DO UPDATE; new: swiezy INSERT -> last_insert_rowid()
            long rid = isNew ? Convert.ToInt64(lastId.ExecuteScalar()) : Convert.ToInt64(existingRid);
            pDelRid.Value = rid; ftsDel.ExecuteNonQuery();
            fRid.Value = rid;
            fSub.Value = m.Subject;
            fBody.Value = m.Body;
            fSend.Value = m.SenderName + " " + m.SenderEmail;
            fRec.Value = m.ToRecips + " " + m.CcRecips;
            ftsIns.ExecuteNonQuery();

            MailAttachmentRepository.UpsertHarvested(c, transaction, m, storageEntryId);

            if (isNew) ins++; else upd++;
        }

        SetMeta(c, transaction, "last_harvest", harvestedAt);
        return new(ins, upd);
    }

    public static void SetMeta(SqliteConnection c, string k, string v)
        => SetMeta(c, null, k, v);

    static void SetMeta(SqliteConnection c, SqliteTransaction? transaction, string k, string v)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=excluded.v;";
        var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; pk.Value = k; cmd.Parameters.Add(pk);
        var pv = cmd.CreateParameter(); pv.ParameterName = "$v"; pv.Value = v; cmd.Parameters.Add(pv);
        cmd.ExecuteNonQuery();
    }

    public static long Count(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM mails;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public static int DeleteByEntryIds(SqliteConnection c, IEnumerable<string> entryIds)
    {
        int deleted = 0;
        using var tx = c.BeginTransaction();
        using var find = c.CreateCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT rowid FROM mails WHERE entry_id=$id;";
        var findId = find.CreateParameter(); findId.ParameterName = "$id"; find.Parameters.Add(findId);

        using var delFts = c.CreateCommand();
        delFts.Transaction = tx;
        delFts.CommandText = "DELETE FROM mails_fts WHERE rowid=$rid;";
        var rid = delFts.CreateParameter(); rid.ParameterName = "$rid"; delFts.Parameters.Add(rid);

        using var delMail = c.CreateCommand();
        delMail.Transaction = tx;
        delMail.CommandText = "DELETE FROM mails WHERE entry_id=$id;";
        var delId = delMail.CreateParameter(); delId.ParameterName = "$id"; delMail.Parameters.Add(delId);

        foreach (string id in entryIds.Distinct(StringComparer.Ordinal))
        {
            findId.Value = id;
            object? rowId = find.ExecuteScalar();
            if (rowId is null or DBNull) continue;
            rid.Value = rowId; delFts.ExecuteNonQuery();
            delId.Value = id; deleted += delMail.ExecuteNonQuery();
        }
        tx.Commit();
        return deleted;
    }

    public static int DeleteByStoreId(SqliteConnection c, string storeId)
    {
        var ids = new List<string>();
        using (var select = c.CreateCommand())
        {
            select.CommandText = "SELECT entry_id FROM mails WHERE store_id=$store;";
            select.Parameters.AddWithValue("$store", storeId);
            using var reader = select.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return DeleteByEntryIds(c, ids);
    }

    /// <summary>Czas ostatniego harvestu (meta 'last_harvest', "yyyy-MM-dd HH:mm:ss") albo null.</summary>
    public static string? LastHarvest(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k='last_harvest';";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Przelicza kolumne 'kind' dla WSZYSTKICH wierszy wg [[NoiseRules]] (config, w C# = jedno
    /// zrodlo prawdy z harvestem). Zwraca ile 'alert'. FTS bez zmian - filtr jest na tabeli mails.</summary>
    public static int Reclassify(SqliteConnection c)
    {
        NoiseRules.Reload(); // swieze reguly z pliku
        // (rowid, folder_leaf, sender_email, sender_name, obecny kind) - by aktualizowac TYLKO zmienione wiersze
        var rows = new List<(long Rid, string Fl, string Se, string Sn, string Cur)>();
        using (var sel = c.CreateCommand())
        {
            sel.CommandText = "SELECT rowid, folder_leaf, sender_email, sender_name, kind FROM mails;";
            using var r = sel.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                          r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4)));
        }
        using var tx = c.BeginTransaction();
        using var upd = c.CreateCommand();
        upd.CommandText = "UPDATE mails SET kind=$k WHERE rowid=$r;";
        var pk = upd.CreateParameter(); pk.ParameterName = "$k"; upd.Parameters.Add(pk);
        var pr = upd.CreateParameter(); pr.ParameterName = "$r"; upd.Parameters.Add(pr);
        int alerts = 0;
        foreach (var row in rows)
        {
            string kind = Classify.Kind(row.Fl, row.Se, row.Sn);
            if (kind == "alert") alerts++;
            if (kind != row.Cur) { pk.Value = kind; pr.Value = row.Rid; upd.ExecuteNonQuery(); } // pomijamy niezmienione (zwykle wiekszosc)
        }
        tx.Commit();
        return alerts;
    }
}

/// <summary>Klasyfikacja maila: 'mail' (korespondencja) vs 'alert' (szum) - deterministycznie wg [[NoiseRules]].</summary>
static class Classify
{
    public static string Kind(HarvestedMail m) => Kind(m.FolderLeaf, m.SenderEmail, m.SenderName);

    public static string Kind(string? folderLeaf, string? senderEmail, string? senderName) =>
        NoiseRules.Load().IsNoise(folderLeaf, senderEmail, senderName) ? "alert" : "mail";
}
