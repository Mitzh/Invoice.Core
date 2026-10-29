using System.Text;
using System.Text.RegularExpressions;

namespace Mitzh.Invoice.Core;

internal static class InvoiceDrawerReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    internal static string Extract(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC).Trim();
        if (!Regex.IsMatch(text, @"^(?:开\s*票|收款人\s*:|复核人?\s*:)", RegexOptions.None, Timeout))
        {
            return null;
        }

        Match match = Regex.Match(text, @"开\s*票\s*(?:人|员)\s*:?\s*(.+?)(?=\s*(?:收款人|复核人?|收款|销售方|购买方)\s*:|$)", RegexOptions.None, Timeout);
        if (!match.Success)
        {
            return null;
        }

        string name = Regex.Replace(match.Groups[1].Value.Trim(), @"\s+", " ");
        if (!Regex.IsMatch(name, @"^[\p{L}\p{M}\p{Nd}·• .'\-]{1,60}$", RegexOptions.None, Timeout))
        {
            return null;
        }

        if (name.Any(c => c is >= '\u3400' and <= '\u9FFF') && !name.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            name = name.Replace(" ", "");
        }

        return name;
    }

    public static string Read(IEnumerable<List<TextBox>> rows, List<string> issues)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (List<TextBox> row in rows)
        {
            for (int start = 0; start < row.Count; start++)
            {
                if (!Regex.IsMatch(row[start].Text.TrimStart(), @"^(?:开|收款人|复核)", RegexOptions.None, Timeout))
                {
                    continue;
                }

                var text = new StringBuilder();
                for (int i = start; i < row.Count; i++)
                {
                    if (i > start && (row[i].X - (row[i - 1].X + row[i - 1].Width) > Math.Max(3, Math.Max(row[i].Height, row[i - 1].Height) * 3) || Regex.IsMatch(row[i].Text.TrimStart(), @"^(?:收款人|复核人?|收款|开票人|开票员)\s*[:：]", RegexOptions.None, Timeout)))
                    {
                        break;
                    }

                    if (text.Length > 200)
                    {
                        break;
                    }

                    if (text.Length > 0)
                    {
                        text.Append(' ');
                    }

                    text.Append(row[i].Text);
                }

                if (Extract(text.ToString()) is { } name)
                {
                    names.Add(name);
                }
            }
        }

        if (names.Count > 1)
        {
            issues.Add("检测到多个不同开票人，未自动选择，需复核。");
        }

        return names.Count == 1 ? names.Single() : null;
    }
}
