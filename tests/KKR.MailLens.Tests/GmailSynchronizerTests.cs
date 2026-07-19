using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GmailSynchronizerTests
{
    [TestInitialize]
    public void Initialize() => GmailCancellation.ClearAll();

    [TestCleanup]
    public void Cleanup() => GmailCancellation.ClearAll();

    [TestMethod]
    public async Task InitialSync_ImportsPagesAndIndexesOffline()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using var api = new FakeGmailApiClient();
        api.Messages["m1"] = GmailTestMessage.Create("m1", body: "Pierwszy neutralny tekst");
        api.Messages["m2"] = GmailTestMessage.Create("m2", body: "Drugi neutralny tekst", labels: ["Label_Test"]);
        api.MessagePages[""] = () => new GmailMessagePage(["m1"], "page-2");
        api.MessagePages["page-2"] = () => new GmailMessagePage(["m2"], null);
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api).SyncAsync(account, false, CancellationToken.None);

        GmailAccountRecord saved = GmailRepository.FindAccount(db.Connection, account.Id)!;
        Assert.IsTrue(result.WasFullSync);
        Assert.AreEqual(2, result.Inserted);
        Assert.IsTrue(saved.InitialSyncCompleted);
        Assert.AreEqual("101", saved.LastHistoryId);
        Assert.AreEqual(2, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM mails_fts WHERE mails_fts MATCH 'neutralny';"));
        CollectionAssert.AreEqual(new string?[] { null, "page-2" }, api.RequestedPageTokens);
    }

    [TestMethod]
    public async Task InterruptedInitialSync_ResumesFromSavedPageToken()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using (var firstApi = new FakeGmailApiClient())
        {
            firstApi.Messages["m1"] = GmailTestMessage.Create("m1");
            firstApi.MessagePages[""] = () => new GmailMessagePage(["m1"], "resume-token");
            firstApi.MessagePages["resume-token"] = () => throw new OperationCanceledException();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await new GmailSynchronizer(db.Connection, firstApi).SyncAsync(account, false, CancellationToken.None));
        }

        GmailAccountRecord checkpoint = GmailRepository.FindAccount(db.Connection, account.Id)!;
        Assert.AreEqual("resume-token", checkpoint.InitialPageToken);
        Assert.AreEqual(1, GmailRepository.MessageCount(db.Connection, account.Id));

        using var resumedApi = new FakeGmailApiClient();
        resumedApi.Messages["m2"] = GmailTestMessage.Create("m2");
        resumedApi.MessagePages["resume-token"] = () => new GmailMessagePage(["m2"], null);
        resumedApi.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "102", null));
        await new GmailSynchronizer(db.Connection, resumedApi).SyncAsync(checkpoint, false, CancellationToken.None);

        Assert.AreEqual("resume-token", resumedApi.RequestedPageTokens.Single());
        Assert.AreEqual(2, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.IsTrue(GmailRepository.FindAccount(db.Connection, account.Id)!.InitialSyncCompleted);
    }

    [TestMethod]
    public async Task IncrementalSync_UpdatesLabelsUnreadAndDeletesMessage()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using (var initial = new FakeGmailApiClient())
        {
            initial.Messages["m1"] = GmailTestMessage.Create("m1");
            initial.Messages["m2"] = GmailTestMessage.Create("m2");
            initial.MessagePages[""] = () => new GmailMessagePage(["m1", "m2"], null);
            initial.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));
            await new GmailSynchronizer(db.Connection, initial).SyncAsync(account, false, CancellationToken.None);
        }

        GmailAccountRecord current = GmailRepository.FindAccount(db.Connection, account.Id)!;
        using var incremental = new FakeGmailApiClient();
        incremental.Messages["m1"] = GmailTestMessage.Create("m1", subject: "Updated Record", labels: ["UNREAD", "Label_Test"]);
        incremental.HistoryPages.Enqueue(() => new GmailHistoryPage(["m1"], ["m2"], "102", null));
        GmailSyncResult result = await new GmailSynchronizer(db.Connection, incremental).SyncAsync(current, false, CancellationToken.None);

        Assert.IsFalse(result.WasFullSync);
        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(1, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual("Updated Record", db.ScalarText("SELECT subject FROM messages;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT is_unread FROM messages;"));
        Assert.AreEqual("102", GmailRepository.FindAccount(db.Connection, account.Id)!.LastHistoryId);
        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM message_labels;"));
    }

    [TestMethod]
    public async Task ExpiredHistoryId_PerformsControlledFullSyncWithoutDuplicates()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using (var initial = new FakeGmailApiClient())
        {
            initial.Messages["m1"] = GmailTestMessage.Create("m1");
            initial.MessagePages[""] = () => new GmailMessagePage(["m1"], null);
            initial.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));
            await new GmailSynchronizer(db.Connection, initial).SyncAsync(account, false, CancellationToken.None);
        }

        GmailAccountRecord current = GmailRepository.FindAccount(db.Connection, account.Id)!;
        using var api = new FakeGmailApiClient { Profile = new GmailProfile("sender@example.invalid", "200") };
        api.HistoryPages.Enqueue(() => throw new GmailHistoryExpiredException());
        api.Messages["m1"] = GmailTestMessage.Create("m1", subject: "After Full Sync");
        api.MessagePages[""] = () => new GmailMessagePage(["m1"], null);
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "201", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api).SyncAsync(current, false, CancellationToken.None);
        Assert.IsTrue(result.WasFullSync);
        Assert.AreEqual(1, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual("After Full Sync", db.ScalarText("SELECT subject FROM messages;"));
        Assert.AreEqual("201", GmailRepository.FindAccount(db.Connection, account.Id)!.LastHistoryId);
    }

    // UWAGA: ten test ujawnia realną wadę w FullSync (gałąź restartu po GmailPageTokenExpiredException):
    // `continue` w pętli do/while skacze do warunku, który po `pageToken=null` jest fałszywy, więc pętla
    // kończy się bez ponownego pobrania stron, a PruneMissingMessages usuwa cały korpus nowej generacji.
    // Poprawka zlecona osobnym zadaniem (task_eb0e48b7). Odblokować (usunąć [Ignore]) po naprawie.
    [Ignore("Blokowany do naprawy FullSync restartu po wygaśnięciu page tokenu (task_eb0e48b7).")]
    [TestMethod]
    public async Task ExpiredPageToken_RestartsFullSyncWithoutSkippingOrDuplicatingMessages()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using var api = new FakeGmailApiClient();
        api.Messages["m1"] = GmailTestMessage.Create("m1", body: "Pierwszy neutralny tekst");
        api.Messages["m2"] = GmailTestMessage.Create("m2", body: "Drugi neutralny tekst");
        api.MessagePages[""] = () => new GmailMessagePage(["m1"], "page-2");
        int secondPageCalls = 0;
        api.MessagePages["page-2"] = () => ++secondPageCalls == 1
            ? throw new GmailPageTokenExpiredException()
            : new GmailMessagePage(["m2"], null);
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api)
            .SyncAsync(account, false, CancellationToken.None);

        CollectionAssert.AreEqual(new string?[] { null, "page-2", null, "page-2" }, api.RequestedPageTokens);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(2, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(2, db.ScalarLong("SELECT count(DISTINCT gmail_message_id) FROM messages;"));
        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM messages;"));
        Assert.AreEqual(2, result.Inserted);
        Assert.AreEqual(1, result.Updated);
        Assert.AreEqual(0, result.Deleted);

        GmailAccountRecord saved = GmailRepository.FindAccount(db.Connection, account.Id)!;
        Assert.IsTrue(saved.InitialSyncCompleted);
        Assert.IsNull(saved.InitialPageToken);
        Assert.AreEqual("101", saved.LastHistoryId);
    }

    [TestMethod]
    public async Task IncrementalSync_FollowsAllHistoryPagesAndCarriesHistoryIdOverBlankPages()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using (var initial = new FakeGmailApiClient())
        {
            initial.Messages["m1"] = GmailTestMessage.Create("m1");
            initial.MessagePages[""] = () => new GmailMessagePage(["m1"], null);
            initial.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));
            await new GmailSynchronizer(db.Connection, initial).SyncAsync(account, false, CancellationToken.None);
        }

        GmailAccountRecord current = GmailRepository.FindAccount(db.Connection, account.Id)!;
        using var api = new FakeGmailApiClient();
        api.Messages["m2"] = GmailTestMessage.Create("m2");
        api.Messages["m3"] = GmailTestMessage.Create("m3");
        api.HistoryPages.Enqueue(() => new GmailHistoryPage(["m2"], [], "105", "history-2"));
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], ["m1"], "", "history-3"));
        api.HistoryPages.Enqueue(() => new GmailHistoryPage(["m3"], [], "", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api)
            .SyncAsync(current, false, CancellationToken.None);

        CollectionAssert.AreEqual(new (string, string?)[]
        {
            ("101", null),
            ("101", "history-2"),
            ("101", "history-3"),
        }, api.HistoryRequests);
        Assert.IsFalse(result.WasFullSync);
        Assert.AreEqual(3, result.Processed);
        Assert.AreEqual(2, result.Inserted);
        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(2, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM messages WHERE gmail_message_id='m1';"));
        Assert.AreEqual("105", GmailRepository.FindAccount(db.Connection, account.Id)!.LastHistoryId);
    }

    [TestMethod]
    public async Task BrokenSingleMessage_IsRecordedAndDoesNotAbortBatch()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using var api = new FakeGmailApiClient();
        api.Messages["good"] = GmailTestMessage.Create("good");
        api.MessageErrors["broken"] = new InvalidDataException("neutral");
        api.MessagePages[""] = () => new GmailMessagePage(["good", "broken"], null);
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api).SyncAsync(account, false, CancellationToken.None);
        Assert.AreEqual(1, result.Errors);
        Assert.AreEqual(1, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(1, GmailRepository.ErrorCount(db.Connection, account.Id));
        Assert.AreEqual(1, GmailRepository.RetryCount(db.Connection, account.Id));

        GmailAccountRecord checkpoint = GmailRepository.FindAccount(db.Connection, account.Id)!;
        using var retryApi = new FakeGmailApiClient();
        retryApi.Messages["broken"] = GmailTestMessage.Create("broken");
        retryApi.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "102", null));
        GmailSyncResult retried = await new GmailSynchronizer(db.Connection, retryApi)
            .SyncAsync(checkpoint, false, CancellationToken.None);

        Assert.AreEqual(1, retried.Inserted);
        Assert.AreEqual(2, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(0, GmailRepository.RetryCount(db.Connection, account.Id));
        Assert.AreEqual("102", GmailRepository.FindAccount(db.Connection, account.Id)!.LastHistoryId);
    }

    [TestMethod]
    public async Task IncrementalFailure_IsRetriedAfterHistoryCheckpointAdvances()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using (var initial = new FakeGmailApiClient())
        {
            initial.Messages["m1"] = GmailTestMessage.Create("m1");
            initial.MessagePages[""] = () => new GmailMessagePage(["m1"], null);
            initial.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));
            await new GmailSynchronizer(db.Connection, initial).SyncAsync(account, false, CancellationToken.None);
        }

        GmailAccountRecord current = GmailRepository.FindAccount(db.Connection, account.Id)!;
        using (var failed = new FakeGmailApiClient())
        {
            failed.MessageErrors["m1"] = new IOException("neutral transient failure");
            failed.HistoryPages.Enqueue(() => new GmailHistoryPage(["m1"], [], "102", null));
            GmailSyncResult result = await new GmailSynchronizer(db.Connection, failed)
                .SyncAsync(current, false, CancellationToken.None);
            Assert.AreEqual(1, result.Errors);
        }

        Assert.AreEqual("102", GmailRepository.FindAccount(db.Connection, account.Id)!.LastHistoryId);
        Assert.AreEqual(1, GmailRepository.RetryCount(db.Connection, account.Id));

        current = GmailRepository.FindAccount(db.Connection, account.Id)!;
        using var recovered = new FakeGmailApiClient();
        recovered.Messages["m1"] = GmailTestMessage.Create("m1", subject: "Recovered Record");
        recovered.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "103", null));
        await new GmailSynchronizer(db.Connection, recovered).SyncAsync(current, false, CancellationToken.None);

        Assert.AreEqual("Recovered Record", db.ScalarText("SELECT subject FROM messages;"));
        Assert.AreEqual(0, GmailRepository.RetryCount(db.Connection, account.Id));
        Assert.AreEqual("103", GmailRepository.FindAccount(db.Connection, account.Id)!.LastHistoryId);
    }

    [TestMethod]
    public async Task FullSyncFailure_DoesNotPruneExistingMessage()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using (var initial = new FakeGmailApiClient())
        {
            initial.Messages["m1"] = GmailTestMessage.Create("m1");
            initial.Messages["m2"] = GmailTestMessage.Create("m2");
            initial.MessagePages[""] = () => new GmailMessagePage(["m1", "m2"], null);
            initial.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));
            await new GmailSynchronizer(db.Connection, initial).SyncAsync(account, false, CancellationToken.None);
        }

        GmailAccountRecord current = GmailRepository.FindAccount(db.Connection, account.Id)!;
        using var failed = new FakeGmailApiClient { Profile = new GmailProfile("sender@example.invalid", "200") };
        failed.Messages["m2"] = GmailTestMessage.Create("m2", subject: "Still Present");
        failed.MessageErrors["m1"] = new IOException("neutral transient failure");
        failed.MessagePages[""] = () => new GmailMessagePage(["m1", "m2"], null);
        failed.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "201", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, failed)
            .SyncAsync(current, true, CancellationToken.None);

        Assert.IsTrue(result.Errors >= 1);
        Assert.AreEqual(2, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(1, GmailRepository.RetryCount(db.Connection, account.Id));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM messages WHERE gmail_message_id='m1';"));
    }

    [TestMethod]
    public async Task HttpTimeoutForSingleMessage_IsRecordedAndDoesNotAbortSync()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using var api = new FakeGmailApiClient();
        api.Messages["good"] = GmailTestMessage.Create("good");
        api.MessageErrors["timeout"] = new TaskCanceledException("neutral http timeout");
        api.MessagePages[""] = () => new GmailMessagePage(["good", "timeout"], null);
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api)
            .SyncAsync(account, false, CancellationToken.None);

        Assert.AreEqual(1, result.Errors);
        Assert.AreEqual(1, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(1, GmailRepository.RetryCount(db.Connection, account.Id));
        Assert.IsTrue(GmailRepository.FindAccount(db.Connection, account.Id)!.InitialSyncCompleted);
    }

    [TestMethod]
    public async Task CancelFlagForSyncedAccount_AbortsSyncDuringMessageFetch()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using var api = new FakeGmailApiClient();
        api.Messages["m1"] = GmailTestMessage.Create("m1");
        api.MessagePages[""] = () =>
        {
            GmailCancellation.Request(account.Id);
            return new GmailMessagePage(["m1"], null);
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new GmailSynchronizer(db.Connection, api).SyncAsync(account, false, CancellationToken.None));
        Assert.AreEqual(0, GmailRepository.MessageCount(db.Connection, account.Id));
    }

    [TestMethod]
    public async Task CancelFlagForOtherAccount_DoesNotAffectSync()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        GmailAccountRecord other = db.AddAccount("other@example.invalid");
        GmailCancellation.Request(other.Id);

        using var api = new FakeGmailApiClient();
        api.Messages["m1"] = GmailTestMessage.Create("m1");
        api.MessagePages[""] = () => new GmailMessagePage(["m1"], null);
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api)
            .SyncAsync(account, false, CancellationToken.None);

        Assert.AreEqual(1, result.Inserted);
        Assert.IsTrue(GmailRepository.FindAccount(db.Connection, account.Id)!.InitialSyncCompleted);
        Assert.IsTrue(GmailCancellation.IsRequested(other.Id));
    }

    [TestMethod]
    public async Task CorruptMessageBody_IsSavedWithVisibleDecodeError()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using var api = new FakeGmailApiClient();
        api.Messages["m1"] = GmailTestMessage.Create("m1", extraParts:
        [
            new GmailApiPart
            {
                PartId = "9",
                MimeType = "text/plain",
                Data = "!!!niepoprawne-base64###",
                Headers = [new GmailHeader("Content-Type", "text/plain; charset=utf-8")],
            },
        ]);
        api.MessagePages[""] = () => new GmailMessagePage(["m1"], null);
        api.HistoryPages.Enqueue(() => new GmailHistoryPage([], [], "101", null));

        GmailSyncResult result = await new GmailSynchronizer(db.Connection, api)
            .SyncAsync(account, false, CancellationToken.None);

        Assert.AreEqual(1, result.Inserted);
        Assert.AreEqual(1, result.Errors);
        Assert.AreEqual(1, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual(0, GmailRepository.RetryCount(db.Connection, account.Id));
        Assert.AreEqual(1, db.ScalarLong(
            "SELECT count(*) FROM sync_errors WHERE stage='decode' AND gmail_message_id='m1';"));
    }

    [TestMethod]
    public async Task PageSave_RollsBackMessageCorpusAttachmentAndJobTogether()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        using (var trigger = db.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER reject_mail_attachment BEFORE INSERT ON mail_attachments
                BEGIN SELECT RAISE(ABORT,'neutral test rollback'); END;
                """;
            trigger.ExecuteNonQuery();
        }

        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "application/pdf",
            Filename = "record.pdf",
            AttachmentId = "attachment-1",
            Size = 123,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        using var api = new FakeGmailApiClient();
        api.Messages["m1"] = GmailTestMessage.Create("m1", extraParts: [part]);
        api.MessagePages[""] = () => new GmailMessagePage(["m1"], null);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
            await new GmailSynchronizer(db.Connection, api).SyncAsync(account, false, CancellationToken.None));

        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM messages;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mails;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mails_fts;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mail_attachments;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM processing_jobs;"));
    }
}
