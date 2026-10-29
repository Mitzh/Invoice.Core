using System.Globalization;
using System.Text.RegularExpressions;
using RapidOcrNet;
using SkiaSharp;

namespace Mitzh.Invoice.Core;

internal static class ImageLayoutRecovery
{
    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
    private static string Header(string s) => Compact(s).Replace("，", "").Replace(",", "").Replace("税率/征收率", "税率");
    private static readonly string[] Columns = ["项目名称", "规格型号", "单位", "数量", "单价", "金额", "税率", "税额"];
    public static async Task<List<TextBox>> RecoverAsync(string path, RapidOcr engine, List<TextBox> originalBoxes, int page, CancellationToken ct)
    {
        InvoiceModel before = InvoiceMapper.Map(originalBoxes).Invoice;
        bool incompleteCells = before.Items.Any(i => i.Quantity.HasValue != i.UnitPrice.HasValue);
        bool inconsistent = before.Warnings.Any(w => w.Contains("不一致"));
        if (before.Items.Count > 0 && before.Amount != null && before.Tax != null && before.IssueDate != null && !incompleteCells && !inconsistent)
        {
            return originalBoxes;
        }

        using var original = SKBitmap.Decode(path);
        if ((long)original.Width * original.Height > 8_000_000)
        {
            return originalBoxes;
        }

        using SKBitmap cleaned = original.Copy();
        SKColor[] pixels = cleaned.Pixels;
        int removed = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 65535) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            SKColor p = pixels[i];
            int high = Math.Max(p.Red, Math.Max(p.Green, p.Blue)), low = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
            if (low >= 110 && low < 245 && high - low <= 25)
            {
                pixels[i] = SKColors.White;
                removed++;
            }
        }

        // A second pass is reserved for incomplete pages with a material pale-neutral foreground.
        if (removed < pixels.Length * .002)
        {
            return originalBoxes;
        }

        cleaned.Pixels = pixels;
        double scale = Math.Min(2, 2048d / Math.Max(original.Width, original.Height));
        using var enlarged = new SKBitmap((int)(original.Width * scale), (int)(original.Height * scale));
        using (var canvas = new SKCanvas(enlarged))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(cleaned, new SKRect(0, 0, enlarged.Width, enlarged.Height));
        }

        OcrResult pass = await engine.DetectAsync(enlarged, RapidOcrOptions.Default with
        {
            ImgResize = 2048,
            Padding = 20,
            UnClipRatio = 1.3f
        }, null, ct);
        var boxes = pass.TextBlocks.Select(b => new TextBox(page, b.BoxPoints.Min(p => (double)p.X) / scale, b.BoxPoints.Min(p => (double)p.Y) / scale, (b.BoxPoints.Max(p => (double)p.X) - b.BoxPoints.Min(p => (double)p.X)) / scale, (b.BoxPoints.Max(p => (double)p.Y) - b.BoxPoints.Min(p => (double)p.Y)) / scale, b.Text, "rapidocr-enhanced", Score(b.CharScores))).ToList();
        var baseBoxes = originalBoxes.ToList();
        async Task<(string Text, double? Confidence)> Read(SKBitmap source, SKRect rect, int zoom)
        {
            rect = new(Math.Max(0, rect.Left), Math.Max(0, rect.Top), Math.Min(source.Width, rect.Right), Math.Min(source.Height, rect.Bottom));
            int w = (int)Math.Ceiling(rect.Width * zoom), h = (int)Math.Ceiling(rect.Height * zoom);
            if (w < 1 || h < 1 || w > 4096 || (long)w * h > 4_000_000)
            {
                return ("", null);
            }

            using var crop = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(crop))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(source, rect, new SKRect(0, 0, w, h));
            }

            OcrResult r = await engine.DetectAsync(crop, RapidOcrOptions.Default with
            {
                ImgResize = 2048,
                Padding = 20,
                UnClipRatio = 1.2f
            }, null, ct);
            return (r.StrRes.Trim(), Score(r.TextBlocks.SelectMany(b => b.CharScores ?? []).ToArray()));
        }

        if (before.IssueDate == null && boxes.FirstOrDefault(b => Compact(b.Text).StartsWith("开票日期")) is { } dateBox)
        {
            var rect = new SKRect((float)(dateBox.X + dateBox.Width * .3), (float)(dateBox.Y - dateBox.Height * .5), (float)(dateBox.X + dateBox.Width + dateBox.Height), (float)(dateBox.Y + dateBox.Height * 1.5));
            var dates = new List<(string Text, DateOnly Date, double? Score)>();
            foreach (int zoom in new[]
            {
                2,
                4
            }

            )
            {
                (string Text, double? Confidence) r = await Read(original, rect, zoom);
                Match m = Regex.Match(Compact(r.Text), @"(?<!\d)(\d{4}年\d{1,2}月\d{1,2}日)(?!\d)");
                if (m.Success && DateOnly.TryParseExact(m.Groups[1].Value, ["yyyy年M月d日", "yyyy年MM月dd日"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
                {
                    dates.Add((m.Groups[1].Value, d, r.Confidence));
                }
            }

            if (dates.Count == 2 && dates[0].Date == dates[1].Date)
            {
                baseBoxes.Add(dateBox with
                {
                    Text = "开票日期：" + dates[1].Text,
                    Source = "rapidocr-date-retry",
                    Confidence = dates[1].Score
                });
            }
        }

        TextBox first = boxes.FirstOrDefault(b => Header(b.Text) == Columns[0]);
        if (first == null)
        {
            return baseBoxes;
        }

        TextBox[] headers = Columns.Select(c => boxes.FirstOrDefault(b => Header(b.Text) == c && Math.Abs(b.Y - first.Y) < first.Height * .65)).ToArray();
        if (headers.Any(h => h == null))
        {
            return baseBoxes;
        }

        TextBox[] head = headers.Select(h => h).ToArray();
        if (head.Zip(head.Skip(1)).Any(pair => pair.First.X >= pair.Second.X))
        {
            return baseBoxes;
        }

        TextBox sum = boxes.FirstOrDefault(b => Compact(b.Text) is "合" or "合计" && b.Y > first.Y + first.Height);
        if (sum == null)
        {
            return baseBoxes;
        }

        double margin = Math.Max(2, first.Height * .18);
        double[] edges = [0, head[1].X - margin, head[1].X + head[1].Width + margin, head[2].X + head[2].Width + margin, head[3].X + head[3].Width + margin, head[4].X + head[4].Width + margin, head[5].X + head[5].Width + margin, head[6].X + head[6].Width + margin, original.Width];
        if (edges.Zip(edges.Skip(1)).Any(p => p.Second - p.First < 5))
        {
            return baseBoxes;
        }

        TextBox[] amountRows = boxes.Where(b => b.Y > first.Y + first.Height * .6 && b.Y < sum.Y - sum.Height && b.X + b.Width / 2 > edges[5] && b.X + b.Width / 2 < edges[6] && InvoiceMapper.Money(Compact(b.Text)) != null).OrderBy(b => b.Y).ToArray();
        if (amountRows.Length is < 1 or > 30)
        {
            return baseBoxes;
        }

        var recovered = new List<TextBox>();
        recovered.AddRange(head.Select(b => b with { Text = Header(b.Text), Source = "rapidocr-table-retry" }));
        for (int row = 0; row < amountRows.Length; row++)
        {
            TextBox amount = amountRows[row];
            double cy = amount.Y + amount.Height / 2;
            double pitch = amountRows.Length == 1 ? amount.Height * 1.7 : row == 0 ? amountRows[1].Y - amount.Y : amount.Y - amountRows[row - 1].Y;
            double half = Math.Min(pitch * .48, amount.Height * .8);
            var cells = new List<TextBox>();
            for (int col = 0; col < 8; col++)
            {
                ct.ThrowIfCancellationRequested();
                var rect = new SKRect((float)edges[col], (float)(cy - half), (float)edges[col + 1], (float)(cy + half));
                (string Text, double? Confidence) value = await Read(cleaned, rect, 3);
                cells.Add(new(page, edges[col], cy - half, edges[col + 1] - edges[col], half * 2, Compact(value.Text), "rapidocr-table-retry", value.Confidence));
            }

            // Preserve the original item wording when a separately read specification is its exact suffix.
            string spec = cells[1].Text;
            TextBox name = spec.Length == 0 ? null : originalBoxes.FirstOrDefault(b => b.X < edges[1] && Math.Abs(b.Y + b.Height / 2 - cy) < pitch * .6 && Compact(b.Text).EndsWith(spec) && Compact(b.Text).Length > spec.Length);
            if (name != null)
            {
                cells[0] = cells[0] with
                {
                    Text = Compact(name.Text)[..^spec.Length],
                    Confidence = name.Confidence
                };
            }

            recovered.AddRange(cells.Where(c => c.Text.Length > 0));
        }

        recovered.AddRange(boxes.Where(b => Math.Abs(b.Y - sum.Y) < sum.Height * .65));
        double top = Math.Min(first.Y, originalBoxes.Where(b => Compact(b.Text).Contains("项目名称")).Select(b => b.Y).DefaultIfEmpty(first.Y).Min()) - first.Height * .2;
        var candidate = baseBoxes.Select(b => b.Y >= top && b.Y < sum.Y + sum.Height ? b with { DiagnosticOnly = true } : b).Concat(recovered).ToList();
        InvoiceModel invoice = InvoiceMapper.Map(candidate).Invoice;
        if (invoice.Items.Count != amountRows.Length || invoice.Amount == null || invoice.Tax == null || invoice.Items.Any(i => i.Amount == null || i.Tax == null) || Math.Abs(invoice.Items.Sum(i => i.Amount.Value) - invoice.Amount.Value) > .01m || Math.Abs(invoice.Items.Sum(i => i.Tax.Value) - invoice.Tax.Value) > .01m)
        {
            return baseBoxes;
        }

        return candidate;
    }

    private static double? Score(float[] scores) => scores is { Length: > 0 } ? scores.Average(v => (double)v) : null;
}
