using System.Text;

namespace Mitzh.Invoice.Core;

internal static class InvoiceTypeReader
{
    internal const string Special = "电子发票（增值税专用发票）";
    internal const string Ordinary = "电子发票（普通发票）";
    internal static string Compact(string s) => string.Concat(s.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
    internal static bool IsComplete(string type) => type is Special or Ordinary;
    internal static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string t = Compact(text);
        if (!t.Contains("电子发票"))
        {
            return text.Trim();
        }

        bool special = t.Contains("专用发票"), ordinary = t.Contains("普通发票");
        if (special && ordinary)
        {
            return "电子发票";
        }

        return special ? Special : ordinary ? Ordinary : t == "电子发票" ? "电子发票" : text.Trim();
    }

    internal static bool IsMetadata(string text)
    {
        string compact = Compact(text);
        return new[]
        {
            "号码",
            "代码",
            "日期",
            "购买方",
            "销售方",
            "备注",
            "名称:"
        }.Any(compact.Contains);
    }

    internal static List<TextBox> Band(IReadOnlyList<TextBox> boxes, TextBox anchor) => boxes.Where(b => b.Page == anchor.Page && !b.DiagnosticOnly && !IsMetadata(b.Text) && b.Height >= anchor.Height * .35 && Math.Abs((b.Y + b.Height / 2) - (anchor.Y + anchor.Height / 2)) <= Math.Max(anchor.Height, b.Height) * .65).OrderBy(b => b.X).ToList();
    internal static TextBox[] Anchors(IReadOnlyList<TextBox> boxes) => boxes.Where(b => !b.DiagnosticOnly && !IsMetadata(b.Text) && (Compact(b.Text).Contains("电子发票") || Compact(b.Text).StartsWith("电子") || Compact(b.Text) == "电") && b.Y < boxes.Where(x => x.Page == b.Page && (Compact(x.Text).Contains("购买方") || Compact(x.Text).Contains("销售方") || Compact(x.Text).Contains("项目名称") || Compact(x.Text) is "购" or "销")).Select(x => x.Y).DefaultIfEmpty(double.PositiveInfinity).Min() && Compact(string.Concat(Band(boxes, b).Select(x => x.Text))).Contains("电子发票")).ToArray();
    public static string Read(IReadOnlyList<TextBox> boxes, List<string> issues)
    {
        TextBox[] anchors = Anchors(boxes);
        var confirmed = new HashSet<string>();
        foreach (TextBox anchor in anchors)
        {
            string band = Compact(string.Concat(Band(boxes, anchor).Select(b => b.Text)));
            if (band.Contains("专用发票") && band.Contains("普通发票"))
            {
                confirmed.Add(Special);
                confirmed.Add(Ordinary);
            }

            string normalized = Normalize(band);
            if (IsComplete(normalized))
            {
                confirmed.Add(normalized);
            }

            // Check individual titles as well: duplicate/repeated OCR boxes must not hide a conflict.
            string own = Normalize(anchor.Text);
            if (IsComplete(own))
            {
                confirmed.Add(own);
            }
        }

        if (confirmed.Count == 1)
        {
            return confirmed.Single();
        }

        if (confirmed.Count > 1)
        {
            issues.Add("标题中同时存在专票与普票信息，类型未自动选择。");
            return "电子发票";
        }

        if (anchors.Length > 0)
        {
            // Preserve other explicitly named invoice categories; never force them into VAT special/ordinary.
            string other = anchors.Select(a => a.Text).FirstOrDefault(t => new[] { "铁路", "航空", "机动车", "二手车" }.Any(t.Contains));
            if (other != null)
            {
                return other.Trim();
            }

            issues.Add("仅识别到电子发票标题，专票或普票类型尚未确认。");
            return "电子发票";
        }

        return boxes.Select(b => b.Text).FirstOrDefault(s => s.Contains("发票") && !IsMetadata(s));
    }
}
