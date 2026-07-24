using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record MailboxDashboardSource(
    MailboxSourceRecord Source,
    long MessageCount,
    MailboxImportSourceRecord? LatestImport);

sealed record MailboxDashboardSnapshot(
    IReadOnlyList<MailboxDashboardSource> Sources,
    MailboxImportRunRecord? ActiveRun,
    IReadOnlyList<MailboxImportSourceRecord> ActiveRunSources,
    ProcessingPipelineSnapshot? Processing)
{
    public long EnabledSources => Sources.LongCount(item => item.Source.Enabled);
    public long MessageCount => Sources.Sum(item => item.MessageCount);
}

static class MailboxDashboard
{
    public static MailboxDashboardSnapshot Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IReadOnlyDictionary<long, long> messageCounts = ReadMessageCounts(connection);
        MailboxDashboardSource[] sources = MailboxSourceRepository.List(connection)
            .Select(source => new MailboxDashboardSource(
                source,
                messageCounts.GetValueOrDefault(source.Id),
                MailboxImportRunRepository.FindLatestSourceRun(connection, source.Id)))
            .ToArray();
        MailboxImportRunRecord? active = MailboxImportRunRepository.FindActive(connection);
        IReadOnlyList<MailboxImportSourceRecord> activeSources = active is null
            ? []
            : MailboxImportRunRepository.ListSources(connection, active.Id);
        ProcessingPipelineSnapshot? processing = active is null
            ? null
            : ProcessingPipelineStatus.Read(
                connection,
                active.ProcessingJobBaselineId,
                mailboxImportRunId: active.Id);
        return new MailboxDashboardSnapshot(
            sources,
            active,
            activeSources,
            processing);
    }

    static IReadOnlyDictionary<long, long> ReadMessageCounts(SqliteConnection connection)
    {
        var result = new Dictionary<long, long>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mailbox_source_id,count(*)
            FROM mails
            WHERE mailbox_source_id IS NOT NULL
            GROUP BY mailbox_source_id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetInt64(0)] = reader.GetInt64(1);
        return result;
    }
}
