namespace Mitzh.Invoice.Core;
/// <summary>Reusable offline recognizer. Keep one instance for the application's lifetime.</summary>
public sealed class InvoiceRecognizer : IDisposable
{
    private readonly InvoiceRecognitionOptions options;
    private readonly DocumentParser parser;
    private readonly SemaphoreSlim capacity;
    private bool disposed;
    public bool OcrConfigured => parser.OcrConfigured;

    /// <param name = "modelDirectory">Local model folder. Defaults to models/ next to the application's executable.</param>
    public InvoiceRecognizer(string modelDirectory = null) : this(FromDirectory(modelDirectory))
    {
    }

    public InvoiceRecognizer(InvoiceRecognitionOptions options)
    {
        if (options.MaxBytes < 1 || options.MaxPages is < 1 or > 100 || options.Workers is < 1 or > 8 || options.TimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        this.options = options;
        parser = new DocumentParser(options);
        capacity = new(options.Workers, options.Workers);
    }

    private static InvoiceRecognitionOptions FromDirectory(string directory)
    {
        string path = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "models"));
        return new()
        {
            DetModel = Path.Combine(path, "ch_PP-OCRv5_mobile_det.onnx"),
            ClsModel = Path.Combine(path, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            RecModel = Path.Combine(path, "ch_PP-OCRv5_rec_mobile.onnx"),
            Dictionary = Path.Combine(path, "ppocrv5_dict.txt")
        };
    }

    /// <summary>Recognize an XML/OFD/PDF/PNG/JPEG file and return its invoice model.</summary>
    public async Task<InvoiceModel> RecognizeAsync(string filePath, CancellationToken cancellationToken = default) => (await RecognizeDetailedAsync(filePath, cancellationToken)).Invoice;
    /// <summary>Recognize from the current stream position. The caller's stream is left open; seek support and a file name are not required.</summary>
    public async Task<InvoiceModel> RecognizeAsync(Stream stream, CancellationToken cancellationToken = default) => (await RecognizeDetailedAsync(stream, cancellationToken)).Invoice;
    /// <summary>Optional diagnostic entry point including text boxes and parsing issues.</summary>
    public async Task<ParseResult> RecognizeDetailedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = File.OpenRead(filePath);
        return await RecognizeDetailedAsync(stream, cancellationToken);
    }

    public async Task<ParseResult> RecognizeDetailedAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        CancellationToken ct = timeout.Token;
        await capacity.WaitAsync(ct);
        string dir = Path.Combine(Path.GetFullPath(options.TempDirectory), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            byte[] signature = new byte[512];
            int count = 0;
            while (count < signature.Length)
            {
                int n = await stream.ReadAsync(signature.AsMemory(count), ct);
                if (n == 0)
                {
                    break;
                }

                count += n;
            }

            string ext = InvoiceFileFormat.Detect(signature.AsSpan(0, count));
            string path = Path.Combine(dir, "input" + ext);
            await using (FileStream output = File.Create(path))
            {
                if (count > options.MaxBytes)
                {
                    throw new InvalidDataException("File exceeds size limit.");
                }

                await output.WriteAsync(signature.AsMemory(0, count), ct);
                byte[] buffer = new byte[81920];
                long total = count;
                while (true)
                {
                    int n = await stream.ReadAsync(buffer, ct);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                    if (total > options.MaxBytes)
                    {
                        throw new InvalidDataException("File exceeds size limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }

            return await parser.Parse(path, ext, ct);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            finally
            {
                capacity.Release();
            }
        }
    }

    /// <summary>Parse decoded XML text directly; this is distinct from the file-path overload.</summary>
    public async Task<InvoiceModel> RecognizeXmlAsync(string xmlContent, CancellationToken cancellationToken = default) => (await RecognizeXmlDetailedAsync(xmlContent, cancellationToken)).Invoice;
    public async Task<ParseResult> RecognizeXmlDetailedAsync(string xmlContent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlContent);
        if (System.Text.Encoding.UTF8.GetByteCount(xmlContent) > options.MaxBytes)
        {
            throw new InvalidDataException("XML content exceeds size limit.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        await capacity.WaitAsync(timeout.Token);
        try
        {
            return await XmlInvoiceReader.ReadContentAsync(xmlContent, options.MaxBytes, timeout.Token);
        }
        finally
        {
            capacity.Release();
        }
    }

    /// <summary>Dispose after all in-flight recognition calls have completed.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        parser.Dispose();
        capacity.Dispose();
    }
}
