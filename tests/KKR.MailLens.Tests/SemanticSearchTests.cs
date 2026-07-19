using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Http.Json;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class SemanticSearchTests
{
    [TestMethod]
    public void OllamaTransport_DoesNotFollowRedirectsOrUseProxy()
    {
        using SocketsHttpHandler handler = OllamaEmbeddingProvider.CreateDefaultHandler();

        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.IsFalse(handler.UseProxy);
    }

    [TestMethod]
    public void OllamaOptions_RejectNonLoopbackEndpoint()
    {
        Assert.Throws<ArgumentException>(() =>
            new OllamaEmbeddingOptions("https://example.invalid", "neutral-model").Validate());

        Uri endpoint = new OllamaEmbeddingOptions(
            "http://127.0.0.1:11434", "neutral-model").Validate();
        Assert.IsTrue(endpoint.IsLoopback);
    }

    [TestMethod]
    public void OllamaOptions_RejectCredentialsQueryFragmentAndForeignSchemes()
    {
        Assert.Throws<ArgumentException>(() =>
            new OllamaEmbeddingOptions("http://user:pass@127.0.0.1:11434", "neutral-model").Validate());
        Assert.Throws<ArgumentException>(() =>
            new OllamaEmbeddingOptions("http://127.0.0.1:11434/?debug=1", "neutral-model").Validate());
        Assert.Throws<ArgumentException>(() =>
            new OllamaEmbeddingOptions("http://127.0.0.1:11434/#fragment", "neutral-model").Validate());
        Assert.Throws<ArgumentException>(() =>
            new OllamaEmbeddingOptions("ftp://127.0.0.1:11434", "neutral-model").Validate());
        Assert.Throws<ArgumentException>(() =>
            new OllamaEmbeddingOptions("http://[::2]:11434", "neutral-model").Validate());
    }

    [TestMethod]
    public async Task OllamaProvider_RealDefaultTransportDoesNotFollowRedirects()
    {
        using var server = new RedirectingHttpServer();
        using var provider = new OllamaEmbeddingProvider(new OllamaEmbeddingOptions(
            $"http://127.0.0.1:{server.Port}", "neutral-model", TimeSpan.FromSeconds(15)));

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await provider.EmbedAsync(["Neutralny tekst"]));

        Assert.AreEqual(HttpStatusCode.Found, error.StatusCode);
        CollectionAssert.AreEqual(new[] { "/api/embed" }, server.RequestedPaths);
    }

    [TestMethod]
    public async Task OllamaProvider_UsesLocalEmbedApiAndNormalizesVector()
    {
        var handler = new EmbedHandler();
        using var provider = new OllamaEmbeddingProvider(
            new OllamaEmbeddingOptions("http://localhost:11434", "neutral-model"), handler);

        IReadOnlyList<float[]> result = await provider.EmbedAsync(["Neutralny tekst"]);

        Assert.AreEqual("/api/embed", handler.RequestUri?.AbsolutePath);
        Assert.HasCount(1, result);
        Assert.AreEqual(0.6f, result[0][0], 0.0001f);
        Assert.AreEqual(0.8f, result[0][1], 0.0001f);
    }

    [TestMethod]
    public async Task Index_IsIncrementalAndSemanticSearchRanksByCosineSimilarity()
    {
        using var db = new TestDatabase();
        AddDocument(db, "a", "record-a.txt", "Pojazd wymaga wymiany akumulatora.");
        AddDocument(db, "b", "record-b.txt", "Dokument zawiera neutralne sprawozdanie kwartalne.");
        using var provider = new FakeEmbeddingProvider();

        SemanticIndexResult first = await SemanticIndex.IndexAsync(db.Connection, provider, batchSize: 2);
        SemanticIndexResult second = await SemanticIndex.IndexAsync(db.Connection, provider, batchSize: 2);
        SemanticQueryResult result = await SemanticSearch.SearchAsync(db.Connection, provider,
            "kwestia zasilania pojazdu", limit: 2, hybrid: false);

        Assert.AreEqual(2, first.Indexed);
        Assert.AreEqual(2, first.TotalForModel);
        Assert.AreEqual(0, second.Indexed);
        Assert.HasCount(2, result.Hits);
        Assert.AreEqual("record-a.txt", result.Hits[0].Hit.Filename);
        Assert.IsGreaterThan(result.Hits[1].Similarity ?? -1, result.Hits[0].Similarity ?? -1);
    }

    [TestMethod]
    public async Task HybridSearchCombinesExactFtsAndSemanticChannel()
    {
        using var db = new TestDatabase();
        AddDocument(db, "a", "record-a.txt", "Pojazd wymaga wymiany akumulatora.");
        AddDocument(db, "b", "record-b.txt", "Dokument zawiera neutralne sprawozdanie kwartalne.");
        using var provider = new FakeEmbeddingProvider();
        await SemanticIndex.IndexAsync(db.Connection, provider);

        SemanticQueryResult result = await SemanticSearch.SearchAsync(db.Connection, provider,
            "akumulatora", limit: 2, hybrid: true);

        Assert.AreEqual("record-a.txt", result.Hits[0].Hit.Filename);
        Assert.AreEqual("fts+semantic", result.Hits[0].Channels);
    }

    [TestMethod]
    public async Task EmbeddingsCascadeWithSegmentsAndSchemaIsMigrated()
    {
        using var db = new TestDatabase();
        AddDocument(db, "a", "record-a.txt", "Pojazd wymaga wymiany akumulatora.");
        using var provider = new FakeEmbeddingProvider();
        await SemanticIndex.IndexAsync(db.Connection, provider);
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM content_embeddings;"));
        Assert.AreEqual(Db.SchemaVersion,
            db.ScalarLong("SELECT CAST(v AS INTEGER) FROM meta WHERE k='schema_version';"));

        using var command = db.Connection.CreateCommand();
        command.CommandText = "DELETE FROM content_segments;";
        command.ExecuteNonQuery();

        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM content_embeddings;"));
    }

    static void AddDocument(TestDatabase db, string suffix, string filename, string text)
    {
        GmailAccountRecord account = GmailRepository.FindAccount(db.Connection, 1) ?? db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "part-" + suffix,
            MimeType = "text/plain",
            Filename = filename,
            AttachmentId = "attachment-" + suffix,
            Size = text.Length,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create("semantic-" + suffix, extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, account.Id, [message]);
        using var find = db.Connection.CreateCommand();
        find.CommandText = "SELECT id FROM mail_attachments WHERE provider_attachment_key=$key;";
        find.Parameters.AddWithValue("$key", "attachment-" + suffix);
        long attachmentId = Convert.ToInt64(find.ExecuteScalar());
        long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
            db.Connection, attachmentId, new string(suffix[0], 64), "text/plain");
        ExtractionResult extraction = new ContentExtractionDispatcher().Extract(
            filename, "text/plain", System.Text.Encoding.UTF8.GetBytes(text));
        ContentDocumentRepository.SaveExtraction(db.Connection, documentId, extraction, "plain-text", "1");
    }

    sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "neutral-local-model";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<float[]> vectors = inputs.Select(input =>
                input.Contains("pojazd", StringComparison.OrdinalIgnoreCase)
                    || input.Contains("akumulator", StringComparison.OrdinalIgnoreCase)
                    ? new float[] { 1, 0 }
                    : new float[] { 0, 1 }).ToArray();
            return Task.FromResult(vectors);
        }

        public void Dispose() { }
    }

    /// <summary>Minimalny lokalny serwer HTTP odpowiadający przekierowaniem 302 na każde żądanie.
    /// Rejestruje ścieżki, więc test wykrywa, gdyby transport podążył za Location.</summary>
    sealed class RedirectingHttpServer : IDisposable
    {
        readonly System.Net.Sockets.TcpListener listener;
        readonly Task loop;
        readonly List<string> requestedPaths = new();

        public int Port { get; }
        public string[] RequestedPaths { get { lock (requestedPaths) return requestedPaths.ToArray(); } }

        public RedirectingHttpServer()
        {
            listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(ServeAsync);
        }

        async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync();
                    using System.Net.Sockets.NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);
                    string? requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine)) continue;
                    string[] parts = requestLine.Split(' ');
                    lock (requestedPaths) requestedPaths.Add(parts.Length > 1 ? parts[1] : requestLine);
                    string? line;
                    int contentLength = 0;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                    }
                    var body = new char[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int chunk = await reader.ReadAsync(body, read, contentLength - read);
                        if (chunk <= 0) break;
                        read += chunk;
                    }
                    byte[] response = System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 302 Found\r\n" +
                        $"Location: http://127.0.0.1:{Port}/redirected\r\n" +
                        "Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response);
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            listener.Stop();
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    sealed class EmbedHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { model = "neutral-model", embeddings = new[] { new[] { 3f, 4f } } }),
            });
        }
    }
}
