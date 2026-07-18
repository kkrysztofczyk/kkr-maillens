using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class MediaTranscriptionTests
{
    [TestMethod]
    public void WhisperJsonParser_MapsLanguageTextAndMillisecondOffsets()
    {
        const string json = """
            {
              "result": { "language": "pl" },
              "transcription": [
                {
                  "timestamps": { "from": "00:12:15,000", "to": "00:12:36,000" },
                  "offsets": { "from": 735000, "to": 756000 },
                  "text": "  Neutralny tekst nagrania  "
                }
              ]
            }
            """;

        ExtractionResult result = WhisperJsonParser.Parse(json, "audio/mp4");

        Assert.AreEqual("pl", result.DetectedLanguage);
        Assert.AreEqual("Neutralny tekst nagrania", result.CleanText);
        Assert.HasCount(1, result.Segments);
        Assert.AreEqual(735000L, result.Segments[0].StartMs);
        Assert.AreEqual(756000L, result.Segments[0].EndMs);
    }

    [TestMethod]
    public async Task Pipeline_QueuesTranscriptionAndIndexesTimestampedSegments()
    {
        using var db = new TestDatabase();
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            long attachmentId = AddMediaAttachment(db);
            var store = new EncryptedBlobStore(Path.Combine(directory, "blobs"), new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, [1, 2, 3, 4]);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "audio/mp4");
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "audio/mp4");

            AttachmentExtractionOutcome initial = AttachmentExtractionProcessor.Process(
                db.Connection, store, attachmentId, documentId);
            Assert.AreEqual("needs-transcription", initial.Status);

            await MediaTranscriptionProcessor.ProcessAsync(db.Connection, store, attachmentId, documentId,
                new FakeTranscriber(), CancellationToken.None);

            Assert.AreEqual("completed", db.ScalarText("SELECT status FROM content_documents;"));
            Assert.AreEqual("transcript", db.ScalarText("SELECT document_kind FROM content_documents;"));
            Assert.AreEqual("pl", db.ScalarText("SELECT detected_language FROM content_documents;"));
            Assert.AreEqual("ggml-small.bin", db.ScalarText("SELECT model_name FROM content_documents;"));
            Assert.AreEqual(735000, db.ScalarLong("SELECT start_ms FROM content_segments;"));
            Assert.AreEqual(756000, db.ScalarLong("SELECT end_ms FROM content_segments;"));
            ContentSearchHit hit = ContentSearch.Search(db.Connection, "neutralny tekst nagrania").Single();
            Assert.AreEqual(735000L, hit.StartMs);
            Assert.AreEqual(756000L, hit.EndMs);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Toolchain_StreamsMediaParsesJsonAndRemovesPlaintextWorkspace()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-transcription-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string ffmpeg = Path.Combine(directory, "fake-ffmpeg.cmd");
            File.WriteAllText(ffmpeg, """
                @echo off
                set "out="
                :loop
                if "%~1"=="" goto done
                set "out=%~1"
                shift
                goto loop
                :done
                more > "%out%"
                """.Replace("\n", "\r\n", StringComparison.Ordinal));
            string whisper = Path.Combine(directory, "fake-whisper.cmd");
            File.WriteAllText(whisper, """
                @echo off
                set "prefix="
                :loop
                if "%~1"=="" goto fail
                if /I "%~1"=="-of" goto write
                shift
                goto loop
                :write
                shift
                > "%~1.json" echo {"result":{"language":"pl"},"transcription":[{"offsets":{"from":1000,"to":2000},"text":"Neutralny tekst nagrania"}]}
                exit /b 0
                :fail
                exit /b 1
                """.Replace("\n", "\r\n", StringComparison.Ordinal));
            string model = Path.Combine(directory, "ggml-small.bin");
            await File.WriteAllBytesAsync(model, [1]);
            string temp = Path.Combine(directory, "temp");
            var transcriber = new FfmpegWhisperTranscriber(new MediaTranscriptionOptions(
                ffmpeg, whisper, model, "auto", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                MaxDurationMinutes: 1, TempDirectory: temp));

            ExtractionResult result = await transcriber.TranscribeAsync([1, 2, 3, 4], "audio/mp4");

            Assert.AreEqual("Neutralny tekst nagrania", result.CleanText);
            Assert.AreEqual(1000L, result.Segments.Single().StartMs);
            Assert.IsFalse(Directory.EnumerateDirectories(temp, "transcription-*").Any());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    static long AddMediaAttachment(TestDatabase db)
    {
        GmailAccountRecord account = db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "audio/mp4",
            Filename = "record.m4a",
            AttachmentId = "attachment-media",
            Size = 4,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create("m-media", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        return db.ScalarLong("SELECT id FROM mail_attachments;");
    }

    sealed class FakeTranscriber : IMediaTranscriber
    {
        public string ModelName => "ggml-small.bin";

        public Task<ExtractionResult> TranscribeAsync(byte[] media, string mimeType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("audio/mp4", mimeType);
            var segment = new ExtractedSegment(0, "Neutralny tekst nagrania", "Neutralny tekst nagrania",
                StartMs: 735000, EndMs: 756000);
            return Task.FromResult(new ExtractionResult(mimeType, segment.RawText, segment.CleanText, false, [segment])
            { DetectedLanguage = "pl" });
        }
    }
}
