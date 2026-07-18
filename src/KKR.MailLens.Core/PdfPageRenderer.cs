using PDFtoImage;
using SkiaSharp;

namespace KKR.MailLens;

sealed record PdfRenderOptions(
    int Dpi = 300,
    int MaxPages = 100,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(10);

    public void Validate()
    {
        if (Dpi is < 72 or > 600) throw new ArgumentOutOfRangeException(nameof(Dpi));
        if (MaxPages is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MaxPages));
        if (EffectiveTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
    }
}

sealed record RenderedPdfPage(int PageNumber, byte[] PngBytes);

interface IPdfPageRenderer
{
    Task<IReadOnlyList<RenderedPdfPage>> RenderAsync(byte[] pdf, IReadOnlyList<int> pageNumbers,
        PdfRenderOptions options, CancellationToken cancellationToken = default);
}

sealed class PdfiumPageRenderer : IPdfPageRenderer
{
    public async Task<IReadOnlyList<RenderedPdfPage>> RenderAsync(byte[] pdf,
        IReadOnlyList<int> pageNumbers, PdfRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(pageNumbers);
        ArgumentNullException.ThrowIfNull(options);
        if (pdf.Length == 0) throw new InvalidDataException("Dokument PDF jest pusty.");
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();

        int[] selected = pageNumbers.Distinct().Order().ToArray();
        if (selected.Length == 0) return [];
        if (selected.Length > options.MaxPages)
            throw new InvalidDataException($"Dokument wymaga OCR dla {selected.Length} stron; limit wynosi {options.MaxPages}.");

        int pageCount = Conversion.GetPageCount(pdf, password: null);
        if (selected.Any(page => page < 1 || page > pageCount))
            throw new ArgumentOutOfRangeException(nameof(pageNumbers), "Numer strony PDF jest poza zakresem.");

        using var timeout = new CancellationTokenSource(options.EffectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var rendered = new List<RenderedPdfPage>(selected.Length);
        try
        {
            var renderOptions = new RenderOptions(Dpi: options.Dpi, Grayscale: true);
            int index = 0;
            await foreach (SKBitmap bitmap in Conversion.ToImagesAsync(pdf,
                selected.Select(page => page - 1), password: null, renderOptions, linked.Token)
                .ConfigureAwait(false))
            {
                using (bitmap)
                using (SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                    rendered.Add(new RenderedPdfPage(selected[index++], encoded.ToArray()));
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            Zero(rendered);
            throw new TimeoutException($"Renderowanie PDF przekroczyło limit {options.EffectiveTimeout.TotalSeconds:0} s.");
        }
        catch
        {
            Zero(rendered);
            throw;
        }

        if (rendered.Count != selected.Length)
        {
            Zero(rendered);
            throw new InvalidDataException("Renderer PDF nie zwrócił wszystkich wybranych stron.");
        }
        return rendered;
    }

    static void Zero(IEnumerable<RenderedPdfPage> pages)
    {
        foreach (RenderedPdfPage page in pages)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(page.PngBytes);
    }
}
