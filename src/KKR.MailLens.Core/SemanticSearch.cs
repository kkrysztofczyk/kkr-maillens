using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

interface IEmbeddingProvider : IDisposable
{
    string Model { get; }
    /// <summary>Zwraca skończone wektory znormalizowane do długości jednostkowej (kontrakt dostawcy).</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}

sealed record OllamaEmbeddingOptions(string Endpoint, string Model, TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(5);

    public Uri Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !endpoint.IsLoopback || endpoint.UserInfo.Length > 0
            || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException(
                "Endpoint embeddingów musi być lokalnym adresem HTTP(S) loopback.", nameof(Endpoint));
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200 || Model.Any(char.IsControl))
            throw new ArgumentException("Nieprawidłowa nazwa lokalnego modelu embeddingów.", nameof(Model));
        if (EffectiveTimeout <= TimeSpan.Zero || EffectiveTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        return endpoint;
    }
}

sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    const int MaxBatch = 64;
    const int MaxResponseBytes = 64 * 1024 * 1024;
    readonly HttpClient client;
    readonly Uri embedEndpoint;
    readonly bool ownsClient;

    public string Model { get; }

    public OllamaEmbeddingProvider(OllamaEmbeddingOptions options)
        : this(options, null) { }

    internal OllamaEmbeddingProvider(OllamaEmbeddingOptions options, HttpMessageHandler? handler)
    {
        Uri endpoint = options.Validate();
        embedEndpoint = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/api/embed");
        Model = options.Model.Trim();
        if (handler is null)
        {
            handler = CreateDefaultHandler();
            ownsClient = true;
        }
        client = new HttpClient(handler, disposeHandler: ownsClient)
        {
            Timeout = options.EffectiveTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    internal static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    };

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 1 or > MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(inputs), $"Batch embeddingów musi mieć 1–{MaxBatch} elementów.");
        if (inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Tekst embeddingu nie może być pusty.", nameof(inputs));

        using HttpResponseMessage response = await client.PostAsJsonAsync(embedEndpoint,
            new EmbedRequest(Model, inputs, true), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = DiagnosticText.Limit(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            throw new HttpRequestException(
                $"Lokalny model embeddingów zwrócił HTTP {(int)response.StatusCode}: {detail}",
                null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("Odpowiedź lokalnego modelu embeddingów przekracza limit.");

        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        EmbedResponse? payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (payload?.Embeddings is null || payload.Embeddings.Length != inputs.Count)
            throw new InvalidDataException("Lokalny model zwrócił niepełny batch embeddingów.");

        int dimensions = payload.Embeddings[0]?.Length ?? 0;
        if (dimensions is < 1 or > 16_384)
            throw new InvalidDataException("Lokalny model zwrócił nieprawidłowy wymiar embeddingu.");
        foreach (float[]? vector in payload.Embeddings)
        {
            if (vector is null || vector.Length != dimensions)
                throw new InvalidDataException("Lokalny model zwrócił embeddingi o różnych wymiarach.");
            VectorMath.Normalize(vector);
        }
        return payload.Embeddings;
    }

    public void Dispose() => client.Dispose();

    sealed record EmbedRequest(string Model, IReadOnlyList<string> Input, bool Truncate);
    sealed record EmbedResponse(float[][] Embeddings);
}

static class VectorMath
{
    public static void Normalize(float[] vector, string description = "Embedding")
    {
        float scale = (float)(1 / Math.Sqrt(NormSquared(vector, description)));
        for (int index = 0; index < vector.Length; index++) vector[index] *= scale;
    }

    public static void Validate(float[] vector, string description = "Embedding")
        => NormSquared(vector, description);

    public static double Dot(float[] left, float[] right)
    {
        double result = 0;
        for (int index = 0; index < left.Length; index++) result += left[index] * (double)right[index];
        return result;
    }

    static double NormSquared(float[] vector, string description)
    {
        double normSquared = 0;
        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException($"{description} zawiera wartość niefinitywną.");
            normSquared += value * (double)value;
        }
        if (normSquared <= 0) throw new InvalidDataException($"{description} ma zerową normę.");
        return normSquared;
    }
}

sealed record SemanticIndexResult(int Indexed, int TotalForModel);

static class SemanticIndex
{
    const int MaxInputCharacters = 12_000;

    public static async Task<SemanticIndexResult> IndexAsync(SqliteConnection connection,
        IEmbeddingProvider provider, int batchSize = 16, long? documentId = null, bool rebuild = false,
        CancellationToken cancellationToken = default, Action? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(provider);
        batchSize = Math.Clamp(batchSize, 1, 64);
        if (rebuild) Delete(connection, provider.Model, documentId);

        int indexed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<EmbeddingSource> sources = Pending(connection, provider.Model, batchSize, documentId);
            if (sources.Count == 0) break;
            IReadOnlyList<float[]> vectors = await provider.EmbedAsync(
                sources.Select(source => TextLimit.Take(source.Text, MaxInputCharacters)).ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (vectors.Count != sources.Count)
                throw new InvalidDataException("Lokalny model zwrócił niepełny batch embeddingów.");
            Save(connection, provider.Model, sources, vectors);
            indexed += sources.Count;
            heartbeat?.Invoke();
        }
        return new SemanticIndexResult(indexed, Count(connection, provider.Model, documentId));
    }

    public static int Count(SqliteConnection connection, string model, long? documentId = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = documentId is null
            ? "SELECT count(*) FROM content_embeddings WHERE model=$model;"
            : """
              SELECT count(*) FROM content_embeddings e
              JOIN content_segments s ON s.id=e.segment_id
              WHERE e.model=$model AND s.document_id=$document;
              """;
        command.Parameters.AddWithValue("$model", model);
        if (documentId is not null) command.Parameters.AddWithValue("$document", documentId.Value);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static IReadOnlyList<EmbeddingSource> Pending(SqliteConnection connection, string model,
        int limit, long? documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.clean_text
            FROM content_segments s
            LEFT JOIN content_embeddings e ON e.segment_id=s.id AND e.model=$model
            WHERE e.segment_id IS NULL AND length(trim(s.clean_text))>0
                AND ($document IS NULL OR s.document_id=$document)
            ORDER BY s.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$document", documentId is null ? DBNull.Value : documentId.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<EmbeddingSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new EmbeddingSource(reader.GetInt64(0), reader.GetString(1)));
        return result;
    }

    static void Save(SqliteConnection connection, string model, IReadOnlyList<EmbeddingSource> sources,
        IReadOnlyList<float[]> vectors)
    {
        int dimensions = vectors[0].Length;
        if (vectors.Any(vector => vector.Length != dimensions))
            throw new InvalidDataException("Batch zawiera embeddingi o różnych wymiarach.");
        foreach (float[] embedding in vectors) VectorMath.Validate(embedding);
        int? existingDimensions = ModelDimensions(connection, model);
        if (existingDimensions is not null && existingDimensions != dimensions)
            throw new InvalidDataException(
                $"Model {model} zmienił wymiar embeddingu z {existingDimensions} na {dimensions}; użyj semantic-index --rebuild.");

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_embeddings(segment_id,model,dimensions,vector,created_at)
            VALUES($segment,$model,$dimensions,$vector,$now)
            ON CONFLICT(segment_id,model) DO UPDATE SET dimensions=excluded.dimensions,
                vector=excluded.vector,created_at=excluded.created_at;
            """;
        SqliteParameter segment = command.Parameters.Add("$segment", SqliteType.Integer);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$dimensions", dimensions);
        SqliteParameter vector = command.Parameters.Add("$vector", SqliteType.Blob);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        for (int index = 0; index < sources.Count; index++)
        {
            segment.Value = sources[index].SegmentId;
            vector.Value = Serialize(vectors[index]);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static int? ModelDimensions(SqliteConnection connection, string model)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dimensions FROM content_embeddings WHERE model=$model LIMIT 1;";
        command.Parameters.AddWithValue("$model", model);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    static void Delete(SqliteConnection connection, string model, long? documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = documentId is null
            ? "DELETE FROM content_embeddings WHERE model=$model;"
            : """
              DELETE FROM content_embeddings WHERE model=$model AND segment_id IN
                  (SELECT id FROM content_segments WHERE document_id=$document);
              """;
        command.Parameters.AddWithValue("$model", model);
        if (documentId is not null) command.Parameters.AddWithValue("$document", documentId.Value);
        command.ExecuteNonQuery();
    }

    static byte[] Serialize(float[] vector)
    {
        byte[] bytes = new byte[checked(vector.Length * sizeof(float))];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    sealed record EmbeddingSource(long SegmentId, string Text);
}

sealed record SemanticSearchHit(ContentSearchHit Hit, double? Similarity, double Score, string Channels);
sealed record SemanticQueryResult(IReadOnlyList<SemanticSearchHit> Hits, int IndexedVectors,
    bool CandidateLimitReached, bool DimensionMismatch = false, int? IndexedDimensions = null,
    int QueryDimensions = 0);

static class SemanticSearch
{
    public static async Task<SemanticQueryResult> SearchAsync(SqliteConnection connection,
        IEmbeddingProvider provider, string query, int limit = 25, bool hybrid = true,
        int maxCandidates = 25_000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SemanticQueryResult([], 0, false);
        limit = Math.Clamp(limit, 1, 500);
        maxCandidates = Math.Clamp(maxCandidates, limit, 250_000);
        IReadOnlyList<float[]> queryVectors = await provider.EmbedAsync([query], cancellationToken)
            .ConfigureAwait(false);
        float[] queryVector = queryVectors.Single();
        VectorMath.Validate(queryVector, "Embedding zapytania");
        int indexed = SemanticIndex.Count(connection, provider.Model);
        int? indexedDimensions = SemanticIndex.ModelDimensions(connection, provider.Model);
        bool dimensionMismatch = indexedDimensions is not null && indexedDimensions.Value != queryVector.Length;
        CandidateScan scan = dimensionMismatch
            ? new CandidateScan([], 0)
            : ScanCandidates(connection, provider.Model, queryVector, Math.Max(limit * 4, limit), maxCandidates);
        bool candidateLimitReached = !dimensionMismatch && indexed > scan.Scanned;
        IReadOnlyList<SemanticSearchHit> semantic = scan.Hits;
        if (!hybrid)
            return new SemanticQueryResult(semantic.Take(limit).ToArray(), indexed, candidateLimitReached,
                dimensionMismatch, indexedDimensions, queryVector.Length);

        IReadOnlyList<ContentSearchHit> lexical;
        try { lexical = ContentSearch.Search(connection, query, Math.Max(limit * 4, limit)); }
        catch (SqliteException) { lexical = []; }
        var fusion = new Dictionary<long, FusionEntry>();
        for (int index = 0; index < semantic.Count; index++)
        {
            SemanticSearchHit hit = semantic[index];
            fusion[hit.Hit.SegmentId] = new FusionEntry(hit.Hit, hit.Similarity,
                1.0 / (60 + index + 1), true, false);
        }
        for (int index = 0; index < lexical.Count; index++)
        {
            ContentSearchHit hit = lexical[index];
            double contribution = 1.25 / (60 + index + 1);
            if (fusion.TryGetValue(hit.SegmentId, out FusionEntry? current))
                fusion[hit.SegmentId] = current with
                {
                    Hit = hit,
                    Score = current.Score + contribution,
                    Lexical = true,
                };
            else
                fusion[hit.SegmentId] = new FusionEntry(hit, null, contribution, false, true);
        }
        SemanticSearchHit[] hits = fusion.Values.OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Hit.Received, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => new SemanticSearchHit(item.Hit, item.Similarity, item.Score,
                item.Semantic && item.Lexical ? "fts+semantic" : item.Semantic ? "semantic" : "fts"))
            .ToArray();
        return new SemanticQueryResult(hits, indexed, candidateLimitReached,
            dimensionMismatch, indexedDimensions, queryVector.Length);
    }

    static CandidateScan ScanCandidates(SqliteConnection connection, string model,
        float[] queryVector, int topK, int maxCandidates)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,m.received,m.subject,COALESCE(NULLIF(m.sender_name,''),m.sender_email),
                COALESCE(a.filename,''),COALESCE(d.detected_mime_type,''),s.page_number,s.slide_number,
                s.sheet_name,s.start_ms,s.end_ms,substr(s.clean_text,1,400),e.vector
            FROM content_embeddings e
            JOIN content_segments s ON s.id=e.segment_id
            JOIN content_documents d ON d.id=s.document_id
            JOIN mails m ON m.entry_id=d.mail_entry_id
            LEFT JOIN mail_attachments a ON a.id=d.attachment_id
            WHERE e.model=$model AND e.dimensions=$dimensions
            ORDER BY m.received DESC,s.id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$dimensions", queryVector.Length);
        command.Parameters.AddWithValue("$limit", maxCandidates);
        int vectorBytes = queryVector.Length * sizeof(float);
        var vector = new float[queryVector.Length];
        IComparer<(double Similarity, string Received)> ranking =
            Comparer<(double Similarity, string Received)>.Create((left, right) =>
            {
                int bySimilarity = left.Similarity.CompareTo(right.Similarity);
                return bySimilarity != 0 ? bySimilarity : string.CompareOrdinal(left.Received, right.Received);
            });
        var top = new PriorityQueue<SemanticSearchHit, (double Similarity, string Received)>(ranking);
        int scanned = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            scanned++;
            byte[] bytes = (byte[])reader[12];
            if (bytes.Length != vectorBytes) continue;
            Buffer.BlockCopy(bytes, 0, vector, 0, vectorBytes);
            (double Similarity, string Received) rank = (VectorMath.Dot(queryVector, vector), Text(reader, 1));
            if (top.Count == topK && top.TryPeek(out _, out (double, string) weakest)
                && ranking.Compare(rank, weakest) <= 0)
                continue;
            var hit = new ContentSearchHit(reader.GetInt64(0), rank.Received, Text(reader, 2),
                Text(reader, 3), Text(reader, 4), Text(reader, 5), NullableInt(reader, 6),
                NullableInt(reader, 7), reader.IsDBNull(8) ? null : reader.GetString(8),
                NullableLong(reader, 9), NullableLong(reader, 10), Text(reader, 11), 0);
            var candidate = new SemanticSearchHit(hit, rank.Similarity, rank.Similarity, "semantic");
            if (top.Count == topK) top.EnqueueDequeue(candidate, rank);
            else top.Enqueue(candidate, rank);
        }
        var hits = new SemanticSearchHit[top.Count];
        for (int index = hits.Length - 1; index >= 0; index--) hits[index] = top.Dequeue();
        return new CandidateScan(hits, scanned);
    }

    static string Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    static int? NullableInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    static long? NullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    sealed record CandidateScan(IReadOnlyList<SemanticSearchHit> Hits, int Scanned);
    sealed record FusionEntry(ContentSearchHit Hit, double? Similarity, double Score,
        bool Semantic, bool Lexical);
}

static class SemanticServices
{
    public static IEmbeddingProvider CreateProvider(AppConfig config) => new OllamaEmbeddingProvider(
        new OllamaEmbeddingOptions(config.EmbeddingEndpoint, config.EmbeddingModel,
            TimeSpan.FromSeconds(Math.Clamp(config.EmbeddingTimeoutSeconds, 10, 3600))));
}
