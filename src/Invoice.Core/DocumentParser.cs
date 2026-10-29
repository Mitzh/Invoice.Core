using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Mitzh.Invoice.Core;

public sealed class DocumentParser(InvoiceRecognitionOptions limits) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RapidOcr ocr;
    public bool OcrConfigured => new[]
    {
        limits.DetModel,
        limits.ClsModel,
        limits.RecModel,
        limits.Dictionary
    }.All(p => p != null && File.Exists(p));

    public async Task<ParseResult> Parse(string path, string extension, CancellationToken ct)
    {
        if (extension == ".xml")
        {
            return await XmlInvoiceReader.ReadFileAsync(path, limits.MaxBytes, ct);
        }

        if (extension == ".ofd" && RailwayOfdReader.Read(path, limits.MaxPages, ct) is { } railway)
        {
            return railway;
        }

        List<TextBox> boxes;
        if (extension == ".ofd")
        {
            boxes = OfdReader.Read(path, limits.MaxPages, ct);
        }
        else if (extension == ".pdf")
        {
            boxes = await Pdf(path, ct);
        }
        else
        {
            boxes = await Image(path, 1, ct);
        }

        if (boxes.Count == 0)
        {
            throw new InvalidDataException("No text found. Image-only OFD requires an OFD renderer; native XML extraction is insufficient.");
        }

        if (boxes.Count > 50000 || boxes.Sum(b => b.Text.Length) > 1000000)
        {
            throw new InvalidDataException("Document text limit exceeded.");
        }

        return InvoiceMapper.Map(boxes);
    }

    private async Task<List<TextBox>> Image(string path, int page, CancellationToken ct)
    {
        if (!OcrConfigured)
        {
            throw new InvalidOperationException("OCR_NOT_CONFIGURED: install matching Chinese detector/classifier/recognizer/dictionary locally and configure Limits paths.");
        }

        await gate.WaitAsync(ct);
        try
        {
            using FileStream stream = File.OpenRead(path);
            using SKCodec codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Invalid image.");
            if (codec.Info.Width > 12000 || codec.Info.Height > 12000 || (long)codec.Info.Width * codec.Info.Height > 40_000_000)
            {
                throw new InvalidDataException("Image exceeds pixel limit.");
            }

            if (ocr == null)
            {
                var engine = new RapidOcr();
                using SessionOptions sessions = RapidOcr.GetDefaultSessionOptions(2);
                try
                {
                    engine.InitModels(limits.DetModel, limits.ClsModel, limits.RecModel, limits.Dictionary, sessions);
                    ocr = engine;
                }
                catch
                {
                    engine.Dispose();
                    throw;
                }
            }

            OcrResult result = await ocr.DetectAsync(path, RapidOcrOptions.Default, null, ct);
            var boxes = result.TextBlocks.Select(b => new TextBox(page, b.BoxPoints.Min(p => (double)p.X), b.BoxPoints.Min(p => (double)p.Y), b.BoxPoints.Max(p => (double)p.X) - b.BoxPoints.Min(p => (double)p.X), b.BoxPoints.Max(p => (double)p.Y) - b.BoxPoints.Min(p => (double)p.Y), b.Text, "rapidocr", b.CharScores is { Length: > 0 } ? b.CharScores.Average(x => (double)x) : null)).ToList();
            await RecoverNumber(path, boxes, ct);
            boxes = await ImageLayoutRecovery.RecoverAsync(path, ocr, boxes, page, ct);
            await RecoverTitle(path, boxes, page, ct);
            return boxes;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecoverTitle(string path, List<TextBox> boxes, int page, CancellationToken ct)
    {
        string type = InvoiceTypeReader.Read(boxes, []);
        if (type != null && type != "电子发票")
        {
            return;
        }

        TextBox anchor = InvoiceTypeReader.Anchors(boxes).FirstOrDefault();
        if (anchor == null)
        {
            return;
        }

        List<TextBox> band = InvoiceTypeReader.Band(boxes, anchor);
        using var original = SKBitmap.Decode(path);
        double pad = Math.Max(8, anchor.Height * .25);
        double right = Math.Max(band.Max(b => b.X + b.Width), anchor.X + Math.Max(anchor.Width * 3, original.Width * .5)) + pad;
        double metadata = boxes.Where(b => b.X > anchor.X + anchor.Width && InvoiceTypeReader.IsMetadata(b.Text) && b.Y < anchor.Y + anchor.Height * 2).Select(b => b.X).DefaultIfEmpty(original.Width).Min();
        right = Math.Min(right, metadata);
        var region = new SKRect((float)Math.Max(0, band.Min(b => b.X) - pad), (float)Math.Max(0, band.Min(b => b.Y) - pad), (float)Math.Min(original.Width, right), (float)Math.Min(original.Height, band.Max(b => b.Y + b.Height) + pad));
        var attempts = new List<(string Type, List<TextBox> Boxes)>();
        foreach (int scale in new[]
        {
            2,
            3
        }

        )
        {
            int w = (int)Math.Ceiling(region.Width * scale), h = (int)Math.Ceiling(region.Height * scale);
            if (w < 1 || h < 1 || w > 4096 || (long)w * h > 4_000_000)
            {
                continue;
            }

            using var crop = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(crop))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(original, region, new SKRect(0, 0, w, h));
            }

            OcrResult result = await ocr.DetectAsync(crop, RapidOcrOptions.Default with
            {
                ImgResize = 2048,
                Padding = 20
            }, null, ct);
            var recovered = result.TextBlocks.Select(b => new TextBox(page, region.Left + b.BoxPoints.Min(p => (double)p.X) / scale, region.Top + b.BoxPoints.Min(p => (double)p.Y) / scale, (b.BoxPoints.Max(p => (double)p.X) - b.BoxPoints.Min(p => (double)p.X)) / scale, (b.BoxPoints.Max(p => (double)p.Y) - b.BoxPoints.Min(p => (double)p.Y)) / scale, b.Text, "rapidocr-title-retry", b.CharScores is { Length: > 0 } ? b.CharScores.Average(x => (double)x) : null)).ToList();
            string candidate = InvoiceTypeReader.Read(recovered, []);
            if (InvoiceTypeReader.IsComplete(candidate))
            {
                attempts.Add((candidate, recovered));
            }
        }

        if (attempts.Count == 2 && attempts[0].Type == attempts[1].Type)
        {
            boxes.AddRange(attempts[1].Boxes);
        }
    }

    private async Task RecoverNumber(string path, List<TextBox> boxes, CancellationToken ct)
    {
        if (boxes.Any(b => InvoiceNumberReader.Extract(b.Text) != null))
        {
            return;
        }

        TextBox anchor = boxes.FirstOrDefault(b => b.Text.Replace(" ", "").Contains("发票号码"));
        if (anchor == null)
        {
            return;
        }

        using var original = SKBitmap.Decode(path);
        double padding = Math.Max(8, anchor.Height * .35);
        var region = new SKRect((float)Math.Max(0, anchor.X - padding), (float)Math.Max(0, anchor.Y - padding), (float)Math.Min(original.Width, anchor.X + anchor.Width + padding), (float)Math.Min(original.Height, anchor.Y + anchor.Height + padding));
        var readings = new List<(string Number, string Raw, double? Confidence)>();
        foreach (int scale in new[]
        {
            2,
            3
        }

        )
        {
            ct.ThrowIfCancellationRequested();
            int width = (int)Math.Ceiling(region.Width * scale), height = (int)Math.Ceiling(region.Height * scale);
            if (width <= 0 || height <= 0 || width > 4096 || (long)width * height > 4_000_000)
            {
                continue;
            }

            using var crop = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(crop))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(original, region, new SKRect(0, 0, width, height));
            }

            OcrResult retry = await ocr.DetectAsync(crop, RapidOcrOptions.Default with
            {
                ImgResize = 2048,
                Padding = 20
            }, null, ct);
            string raw = retry.StrRes.Trim();
            string number = InvoiceNumberReader.Extract(raw);
            if (number == null)
            {
                continue;
            }

            float[] scores = retry.TextBlocks.SelectMany(b => b.CharScores ?? []).ToArray();
            readings.Add((number, raw, scores.Length > 0 ? scores.Average(x => (double)x) : null));
        }

        // Accept only two independent scale runs agreeing on the complete number.
        if (readings.Count == 2 && readings[0].Number == readings[1].Number)
        {
            boxes.Add(anchor with
            {
                Text = readings[1].Raw,
                Source = "rapidocr-number-retry",
                Confidence = readings[1].Confidence
            });
        }
    }

    private async Task<List<TextBox>> Pdf(string path, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(path);
        if (doc.NumberOfPages > limits.MaxPages)
        {
            throw new InvalidDataException("PDF page limit exceeded.");
        }

        var boxes = new List<TextBox>();
        foreach (Page page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            Word[] words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToArray();
            // A page-level choice preserves mixed text/scanned PDF support. Sparse pages go through OCR.
            if (words.Sum(w => w.Text.Length) >= 30)
            {
                var pageBoxes = words.Select(w => new TextBox(page.Number, w.BoundingBox.Left, page.Height - w.BoundingBox.Top, w.BoundingBox.Width, w.BoundingBox.Height, w.Text, "pdf-text")).ToList();
                boxes.AddRange(PdfHeaderRecovery.Recover(page, pageBoxes));
                continue;
            }

            string prefix = path + ".page";
            var start = new ProcessStartInfo(limits.PdfToPpm)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string a in new[]
            {
                "-f",
                page.Number.ToString(),
                "-l",
                page.Number.ToString(),
                "-singlefile",
                "-scale-to",
                "3500",
                "-png",
                path,
                prefix
            }

            )
            {
                start.ArgumentList.Add(a);
            }

            using Process proc = Process.Start(start) ?? throw new InvalidOperationException("PDF renderer did not start.");
            Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
            try
            {
                await proc.WaitForExitAsync(ct);
                await Task.WhenAll(stderr, stdout);
            }
            catch
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                }

                throw;
            }

            if (proc.ExitCode != 0)
            {
                throw new InvalidDataException("PDF rasterization failed; encrypted or malformed PDF is unsupported.");
            }

            try
            {
                boxes.AddRange(await Image(prefix + ".png", page.Number, ct));
            }
            finally
            {
                File.Delete(prefix + ".png");
            }
        }

        return boxes;
    }

    public void Dispose()
    {
        ocr?.Dispose();
        gate.Dispose();
    }
}
