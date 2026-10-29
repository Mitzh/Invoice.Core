using System.Runtime.CompilerServices;
using UglyToad.PdfPig.Content;

[assembly: InternalsVisibleTo("Invoice.Checks")]
namespace Mitzh.Invoice.Core;

internal record PdfTextSpan(int Sequence, double X, double Y, double Width, double Height, double Baseline, double FontSize, string Text);
internal static class PdfHeaderRecovery
{
    public static List<TextBox> Recover(Page page, List<TextBox> words)
    {
        InvoiceModel current = InvoiceMapper.Map(words).Invoice;
        bool needsType = current.Type == null || current.Type == "电子发票";
        if (current.Number != null && current.IssueDate != null && !needsType && current.Drawer != null)
        {
            return words;
        }

        // Preserve each PDF text-show operation's character order. Nearest-neighbour word
        // extraction can interleave an adjacent font size or move a digit across punctuation.
        PdfTextSpan[] spans = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).GroupBy(l => l.TextSequence).SelectMany(g => g.GroupBy(l => Math.Round(l.StartBaseLine.Y, 1)).Select(line =>
        {
            Letter[] letters = line.ToArray();
            double x = letters.Min(l => l.BoundingBox.Left), y = page.Height - letters.Max(l => l.BoundingBox.Top);
            return new PdfTextSpan(g.Key, x, y, letters.Max(l => l.BoundingBox.Right) - x, letters.Max(l => l.BoundingBox.Top) - letters.Min(l => l.BoundingBox.Bottom), page.Height - letters.Average(l => l.StartBaseLine.Y), Math.Abs(letters.Average(l => l.FontSize)), string.Concat(letters.Select(l => l.Value)));
        })).ToArray();
        return words.Concat(Rebuild(spans, page.Number).Where(b => (current.Number == null && b.Text.Contains("发票号码")) || (current.IssueDate == null && b.Text.Contains("开票日期")) || (current.Drawer == null && (b.Text.Contains("开票人") || b.Text.Contains("开票员"))) || (needsType && b.Text.Contains("发票") && !b.Text.Contains("号码") && !b.Text.Contains("日期")))).ToList();
    }

    internal static List<TextBox> Rebuild(IEnumerable<PdfTextSpan> spans, int page)
    {
        var rows = new List<List<PdfTextSpan>>();
        foreach (PdfTextSpan span in spans.OrderBy(s => s.Baseline).ThenBy(s => s.Sequence))
        {
            List<PdfTextSpan> row = rows.LastOrDefault(r => Math.Abs(r.Average(s => s.Baseline) - span.Baseline) <= Math.Max(.4, Math.Min(r.Average(s => s.Height), span.Height) * .3) && Math.Max(r.Average(s => s.FontSize), span.FontSize) <= Math.Max(.1, Math.Min(r.Average(s => s.FontSize), span.FontSize)) * 1.35);
            if (row == null)
            {
                rows.Add([span]);
            }
            else
            {
                row.Add(span);
            }
        }

        return rows.Select(r => new TextBox(page, r.Min(s => s.X), r.Min(s => s.Y), r.Max(s => s.X + s.Width) - r.Min(s => s.X), r.Max(s => s.Y + s.Height) - r.Min(s => s.Y), string.Concat(r.OrderBy(s => s.Sequence).Select(s => s.Text)), "pdf-text-header")).ToList();
    }
}
