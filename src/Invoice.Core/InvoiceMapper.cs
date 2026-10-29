using System.Globalization;
using System.Text.RegularExpressions;

namespace Mitzh.Invoice.Core;

public static class InvoiceMapper
{
    private static string Clean(string s) => s.Replace(" ", "").Replace("：", ":").Trim();
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static string Match(string s, string pattern)
    {
        Match m = Regex.Match(s, pattern, RegexOptions.None, MatchTimeout);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static decimal? Money(string s) => decimal.TryParse(s?.Replace("¥", "").Replace("￥", "").Replace(",", ""), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal d) ? d : null;
    public static ParseResult Map(IReadOnlyList<TextBox> evidence)
    {
        IReadOnlyList<TextBox> boxes = evidence.Where(b => !b.DiagnosticOnly).ToArray();
        var issues = new List<string>();
        var invoice = new InvoiceModel();
        var rows = new List<List<TextBox>>();
        foreach (IGrouping<int, TextBox> page in boxes.Where(b => !string.IsNullOrWhiteSpace(b.Text) && b.Height > 0).GroupBy(b => b.Page).OrderBy(g => g.Key))
        {
            var groups = new List<List<TextBox>>();
            foreach (TextBox b in page.OrderBy(b => b.Y).ThenBy(b => b.X))
            {
                List<TextBox> row = groups.LastOrDefault(r => Math.Abs(r.Average(x => x.Y) - b.Y) <= Math.Max(1, Math.Min(r.Average(x => x.Height), b.Height) * .45));
                if (row == null)
                {
                    groups.Add([b]);
                }
                else
                {
                    row.Add(b);
                }
            }

            rows.AddRange(groups.Select(r => r.OrderBy(b => b.X).ToList()));
        }

        string[] texts = rows.Select(r => Clean(string.Join(" ", r.Select(b => b.Text)))).ToArray();
        string all = string.Join("\n", texts);
        invoice.Drawer = InvoiceDrawerReader.Read(rows, issues);
        invoice.Type = InvoiceTypeReader.Read(boxes, issues);
        string[] numberCandidates = boxes.Select(b => InvoiceNumberReader.Extract(b.Text)).Append(InvoiceNumberReader.Extract(all)).Where(n => n != null).Distinct().ToArray();
        invoice.Number = numberCandidates.Length == 1 ? numberCandidates[0] : numberCandidates.Length == 0 ? InvoiceNumberReader.Extract(all) : null;
        if (numberCandidates.Length > 1)
        {
            issues.Add("检测到多个不同发票号码，需人工复核。");
        }

        if (boxes.Any(b => b.Source == "rapidocr-number-retry"))
        {
            issues.Add("发票号码经局部放大重识别获得，需核对原图。");
        }

        invoice.Code = Match(all, @"发票代码:?(\d{12}|\d{10})(?!\d)");
        invoice.CheckCode = Match(all, @"校验码:?(\d{6,20})(?!\d)");
        string date = Match(all, @"开票日期:?(\d{4}[年/-]\d{1,2}[月/-]\d{1,2}日?)(?!\d)");
        if (DateOnly.TryParseExact(date?.Replace("年", "-").Replace("月", "-").Replace("日", "").Replace('/', '-'), ["yyyy-M-d", "yyyy-MM-dd", "yyyy-M-dd", "yyyy-MM-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
        {
            invoice.IssueDate = day;
        }

        invoice.BuyerName = Match(all, @"购买方(?:名称)?:([^\n]+)");
        invoice.SellerName = Match(all, @"销售方(?:名称)?:([^\n]+)");
        invoice.BuyerTaxId = Match(all, @"购买方(?:纳税人识别号|税号):([A-Z0-9]+)");
        invoice.SellerTaxId = Match(all, @"销售方(?:纳税人识别号|税号):([A-Z0-9]+)");
        invoice.Amount = Money(Match(all, @"(?:不含税金额|金额合计):?([￥¥\-\d,.]+)"));
        invoice.Tax = Money(Match(all, @"税额合计:?([￥¥\-\d,.]+)"));
        // Read a labelled monetary box before row concatenation can append watermark digits.
        const string totalPattern = @"(?:价税合计(?:\(小写\)|（小写）)?|[（(]?小写[）)]?):?[￥¥]?(-?[\d,]+\.\d{2})(?!\d)";
        invoice.Total = boxes.Select(b => Money(Match(Clean(b.Text), totalPattern))).FirstOrDefault(v => v != null) ?? Money(Match(all, totalPattern));
        invoice.Remarks = Match(all, @"备注:([^\n]+)");
        // Common invoice forms put buyer/seller headings in a narrow vertical band.
        // Use section geometry only when explicit inline labels did not provide values.
        foreach (string role in new[]
        {
            "购买方",
            "销售方"
        }

        )
        {
            TextBox anchor = FindRole(boxes, role);
            if (anchor == null)
            {
                continue;
            }

            TextBox opposite = FindRole(boxes, role == "购买方" ? "销售方" : "购买方");
            double right = opposite != null && opposite.Page == anchor.Page && opposite.X > anchor.X ? opposite.X : double.PositiveInfinity;
            if (opposite != null && opposite.Page == anchor.Page && opposite.X < anchor.X && Math.Abs(opposite.Y - anchor.Y) < Math.Max(anchor.Height, opposite.Height) * 2)
            {
                right = anchor.X + (anchor.X - opposite.X);
            }

            double bottom = boxes.Where(b => b.Page == anchor.Page && Clean(b.Text) is "项目名称" or "货物或应税劳务、服务名称").Select(b => b.Y).DefaultIfEmpty(anchor.Y + anchor.Height * 9).Min();
            List<TextBox>[] sectionRows = rows.Where(r => r[0].Page == anchor.Page && r.Average(b => b.Y) >= anchor.Y - anchor.Height && r.Average(b => b.Y) < bottom).Select(r => r.Where(b => b.X > anchor.X + anchor.Width * .5 && b.X < right).ToList()).ToArray();
            string[] section = sectionRows.Select(r => Clean(string.Join("", r.Select(b => b.Text)))).ToArray();
            // A complete OCR field box takes priority over an entire geometric row:
            // tall marginal annotations may share the row but are not part of the party name.
            string name = sectionRows.SelectMany(r => r).Select(b => Match(Clean(b.Text), @"^名称:([^:]+)$")).FirstOrDefault(s => s != null) ?? sectionRows.Select(ReadName).FirstOrDefault(s => s != null);
            string taxId = section.Select(s => Match(s, @"(?:纳税人识别号|统一社会信用代码):?([A-Z0-9]{15,20})")).FirstOrDefault(s => s != null);
            if (role == "购买方")
            {
                invoice.BuyerName ??= name;
                invoice.BuyerTaxId ??= taxId;
            }
            else
            {
                invoice.SellerName ??= name;
                invoice.SellerTaxId ??= taxId;
            }
        }

        List<TextBox> summary = rows.FirstOrDefault(r => Clean(string.Concat(r.Select(b => b.Text))).StartsWith("合计"));
        if (summary != null)
        {
            decimal?[] values = summary.Skip(1).Select(b => SummaryMoney(Clean(b.Text))).Where(v => v != null).ToArray();
            if (values.Length == 2)
            {
                invoice.Amount ??= values[0];
                invoice.Tax ??= values[1];
            }
        }

        if (invoice.Remarks == null)
        {
            TextBox anchor = FindRole(boxes, "备注");
            if (anchor != null)
            {
                List<TextBox> totalRow = rows.LastOrDefault(r => r[0].Page == anchor.Page && r.Any(b => Clean(b.Text).Contains("小写")));
                double top = totalRow?.Max(b => b.Y + b.Height) ?? anchor.Y - anchor.Height * 2;
                double bottom = boxes.Where(b => b.Page == anchor.Page && b.Y > anchor.Y && Clean(b.Text).StartsWith("开票人")).Select(b => b.Y).DefaultIfEmpty(anchor.Y + anchor.Height * 8).Min();
                IEnumerable<string> remarkRows = rows.Where(r => r[0].Page == anchor.Page && r.Min(b => b.Y) > top && r.Max(b => b.Y) < bottom).Select(r => string.Concat(r.Where(b => b.X + b.Width / 2 > anchor.X + anchor.Width).Select(b => b.Text))).Where(s => !string.IsNullOrWhiteSpace(s));
                invoice.Remarks = string.Join("\n", remarkRows);
                if (invoice.Remarks.Length == 0)
                {
                    invoice.Remarks = null;
                }
            }
        }

        // Per-page header-derived column positions, not a fixed token stride.
        string[] headers = ["项目名称", "规格型号", "单位", "数量", "单价", "金额", "税率", "税额"];
        foreach (IGrouping<int, List<TextBox>> pageRows in rows.GroupBy(r => r[0].Page))
        {
            double[] columns = null;
            foreach (List<TextBox> row in pageRows)
            {
                List<TextBox> header = MergeHeader(row);
                TextBox nameHeader = header.FirstOrDefault(b => Clean(b.Text) is "项目名称" or "货物或应税劳务、服务名称");
                if (nameHeader != null)
                {
                    columns = headers.Select((h, i) => i == 0 ? nameHeader.X + nameHeader.Width / 2 : header.FirstOrDefault(b => Clean(b.Text) == h) is { } cell ? cell.X + cell.Width / 2 : double.NaN).ToArray();
                    if (columns.Any(double.IsNaN))
                    {
                        columns = null;
                        issues.Add($"第{pageRows.Key}页表头不完整，未推断明细。");
                    }

                    continue;
                }

                if (columns == null)
                {
                    continue;
                }

                string line = Clean(string.Join("", row.Select(b => b.Text)));
                if (line.StartsWith("合计") || line.Contains("价税合计"))
                {
                    columns = null;
                    continue;
                }

                string[] cells = new string[8];
                Array.Fill(cells, "");
                foreach (TextBox b in row)
                {
                    int col = Enumerable.Range(0, 8).MinBy(i => Math.Abs(b.X + b.Width / 2 - columns[i]));
                    cells[col] += b.Text;
                }

                if (string.IsNullOrWhiteSpace(cells[0]))
                {
                    issues.Add($"第{pageRows.Key}页存在无法归属的明细行。");
                    continue;
                }

                if (Money(cells[5]) == null)
                {
                    // Append a name-only continuation to the preceding item on this page.
                    if (cells.Skip(1).All(string.IsNullOrWhiteSpace) && invoice.Items.LastOrDefault() is { } prior && prior.Page == pageRows.Key)
                    {
                        invoice.Items[^1] = prior with
                        {
                            Name = prior.Name + cells[0]
                        };
                    }
                    else
                    {
                        issues.Add($"第{pageRows.Key}页存在换行或非标准明细，需复核。");
                    }

                    continue;
                }

                invoice.Items.Add(new(cells[0], cells[1], cells[2], Money(cells[3]), Money(cells[4]), Money(cells[5]), cells[6], Money(cells[7]), pageRows.Key));
            }
        }

        InvoiceValidation.Check(invoice, issues);
        if (boxes.Any(b => b.Confidence is < .85))
        {
            issues.Add("存在低置信度 OCR 文本。");
        }

        if (boxes.Any(b => b.Source == "rapidocr-title-retry"))
        {
            issues.Add("发票类型经标题局部重识别确认，需核对原图。");
        }

        if (boxes.Any(b => b.Source == "rapidocr-table-retry"))
        {
            issues.Add("明细经过淡灰水印抑制及逐格重识别，品名和规格仍需对照原图复核。");
        }

        if (boxes.Any(b => b.Source == "rapidocr-date-retry"))
        {
            issues.Add("开票日期经局部重识别获得，需核对原图。");
        }

        issues.Add("当前为通用规则解析；未完成该票种真实样本验收，需人工确认。");
        invoice.Warnings = issues.Distinct().ToList();
        return new(invoice, evidence, invoice.Warnings);
    }

    internal static decimal? SummaryMoney(string text) => Money(text.StartsWith('Y') && Regex.IsMatch(text, @"^Y-?\d+\.\d{2}$") ? text[1..] : text);
    private static string ReadName(List<TextBox> row)
    {
        int label = row.FindIndex(b => Clean(b.Text).StartsWith("名称"));
        if (label < 0)
        {
            return null;
        }

        string text = Clean(row[label].Text);
        TextBox previous = row[label];
        foreach (TextBox next in row.Skip(label + 1))
        {
            if (next.X - (previous.X + previous.Width) > Math.Max(Math.Max(previous.Height, next.Height) * 2, row[label].Width * .75) || next.Height > row[label].Height * 2 || next.Text.Contains(':') || next.Text.Contains('：'))
            {
                break;
            }

            text += Clean(next.Text);
            previous = next;
        }

        return Match(text, @"^名称:?([^:]+?)(?:纳税人识别号|统一社会信用代码|$)");
    }

    private static TextBox FindRole(IReadOnlyList<TextBox> boxes, string label)
    {
        TextBox direct = boxes.FirstOrDefault(b => Clean(b.Text) == label || Clean(b.Text) == label + "信息");
        if (direct != null)
        {
            return direct;
        }

        foreach (TextBox start in boxes.Where(b => Clean(b.Text) == label[..1]))
        {
            TextBox previous = start;
            bool found = true;
            foreach (char character in label[1..])
            {
                TextBox next = boxes.Where(b => b.Page == start.Page && Clean(b.Text) == character.ToString() && Math.Abs(b.X - start.X) < Math.Max(start.Height, 1) && b.Y > previous.Y && b.Y - previous.Y < Math.Max(previous.Height, 1) * 3.5).OrderBy(b => b.Y).FirstOrDefault();
                if (next == null)
                {
                    found = false;
                    break;
                }

                previous = next;
            }

            if (found)
            {
                return start;
            }
        }

        return null;
    }

    private static List<TextBox> MergeHeader(List<TextBox> row)
    {
        string[] names = ["单位", "数量", "单价", "金额", "税额", "税率", "项目名称", "规格型号", "税率/征收率"];
        var result = new List<TextBox>();
        for (int i = 0; i < row.Count; i++)
        {
            TextBox first = row[i];
            string text = Clean(first.Text);
            int end = i;
            for (int j = i + 1; j < Math.Min(i + 5, row.Count) && text.Length < 8; j++)
            {
                if (row[j].X - (row[j - 1].X + row[j - 1].Width) > Math.Max(first.Height, 1) * 2.5)
                {
                    break;
                }

                string candidate = text + Clean(row[j].Text);
                if (!names.Any(n => n.StartsWith(candidate)))
                {
                    break;
                }

                text = candidate;
                end = j;
            }

            if (text == "税率/征收率")
            {
                text = "税率";
            }

            result.Add(first with
            {
                Text = text,
                Width = row[end].X + row[end].Width - first.X
            });
            i = end;
        }

        return result;
    }
}
