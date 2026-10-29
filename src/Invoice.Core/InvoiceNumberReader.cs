using System.Text;
using System.Text.RegularExpressions;

namespace Mitzh.Invoice.Core;

internal static class InvoiceNumberReader
{
    // Normalize Unicode presentation and whitespace; never insert, remove or guess digits.
    public static string Extract(string text)
    {
        text = string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
        Match match = Regex.Match(text, @"发票号码:?([0-9]{20}|[0-9]{8})(?![0-9])", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups[1].Value : null;
    }
}
