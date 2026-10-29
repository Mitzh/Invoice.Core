using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mitzh.Invoice.Core;

internal static class XmlInvoiceReader
{
    static XmlInvoiceReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static XmlReaderSettings Settings(long limit) => new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = limit,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        CloseInput = true
    };
    public static Task<ParseResult> ReadFileAsync(string path, long limit, CancellationToken ct) => ReadAsync(() => XmlReader.Create(path, Settings(limit)), ct);
    public static Task<ParseResult> ReadContentAsync(string xml, long limit, CancellationToken ct) => ReadAsync(() => XmlReader.Create(new StringReader(xml.TrimStart('\uFEFF')), Settings(limit)), ct);
    private static async Task<ParseResult> ReadAsync(Func<XmlReader> open, CancellationToken ct)
    {
        // Bound depth/node count before constructing the tree; external resources are never resolved.
        using (XmlReader probe = open())
        {
            int nodes = 0;
            while (await probe.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (probe.Depth > 64 || ++nodes > 100000)
                {
                    throw new InvalidDataException("XML depth/node limit exceeded.");
                }
            }
        }

        using XmlReader reader = open();
        XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        XElement root = document.Root ?? throw new InvalidDataException("XML has no root element.");
        if (!string.Equals(root.Name.LocalName, "EInvoice", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unsupported invoice XML. Expected EInvoice, not arbitrary XML or XBRL.");
        }

        var issues = new List<string>();
        var fields = new List<StructuredFieldEvidence>();
        IEnumerable<XElement> Children(XElement parent, params string[] names) => parent?.Elements().Where(e => e.Name.Namespace == parent.Name.Namespace && names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)) ?? [];
        XElement Section(XElement parent, params string[] names)
        {
            XElement[] values = Children(parent, names).ToArray();
            if (values.Length > 1)
            {
                throw new InvalidDataException("Duplicate XML invoice section: " + names[0]);
            }

            return values.SingleOrDefault();
        }

        string PathOf(XElement element) => "/" + string.Join("/", element.AncestorsAndSelf().Reverse().Select(e => e.Name.LocalName + "[" + (e.ElementsBeforeSelf(e.Name).Count() + 1).ToString(CultureInfo.InvariantCulture) + "]"));
        string Value(XElement parent, params string[] aliases)
        {
            XElement[] matches = Children(parent, aliases).ToArray();
            foreach (XElement e in matches)
            {
                if (e.HasElements)
                {
                    throw new InvalidDataException("Expected scalar XML invoice field.");
                }

                if (e.Value.Length > 65536)
                {
                    throw new InvalidDataException("XML scalar field limit exceeded.");
                }

                fields.Add(new(PathOf(e), e.Value));
            }

            string[] values = matches.Select(e => e.Value.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > 1)
            {
                issues.Add($"XML 字段 {aliases[0]} 存在冲突值，未自动选择。");
                return null;
            }

            if (matches.Length > 1)
            {
                issues.Add($"XML 字段 {aliases[0]} 重复，需复核。");
            }

            return values.SingleOrDefault();
        }

        decimal? Number(XElement parent, bool required, params string[] aliases)
        {
            string value = Value(parent, aliases);
            if (value == null)
            {
                if (required)
                {
                    issues.Add($"XML 缺少金额字段 {aliases[0]}。");
                }

                return null;
            }

            if (value.Length <= 128 && Regex.IsMatch(value, @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$", RegexOptions.None, TimeSpan.FromMilliseconds(100)) && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number) && CanonicalNumber(value) == CanonicalNumber(number.ToString(CultureInfo.InvariantCulture)))
            {
                return number;
            }

            issues.Add($"XML 数值字段 {aliases[0]} 格式无效或超出范围。");
            return null;
        }

        XElement header = Section(root, "Header");
        XElement data = Section(root, "EInvoiceData") ?? throw new InvalidDataException("Missing EInvoiceData.");
        XElement tax = Section(root, "TaxSupervisionInfo");
        XElement seller = Section(data, "SellerInformation");
        XElement buyer = Section(data, "BuyerInformation");
        XElement basic = Section(data, "BasicInformation");
        XElement additional = Section(data, "AdditionalInformation");
        XElement labels = Section(header, "InherentLabel");
        string vat = Value(Section(labels, "GeneralOrSpecialVAT"), "LabelName");
        string type = Value(Section(labels, "EInvoiceType"), "LabelName");
        string invoiceType = vat != null ? type != null && !vat.StartsWith(type, StringComparison.Ordinal) ? $"{type}（{vat}）" : vat : type;
        var invoice = new InvoiceModel
        {
            Type = InvoiceTypeReader.Normalize(invoiceType ?? Value(basic, "InvoiceTypeName")),
            Number = Value(tax, "InvoiceNumber"),
            Code = Value(tax, "InvoiceCode"),
            CheckCode = Value(tax, "CheckCode"),
            Drawer = Value(basic, "Drawer", "DrawerName"),
            BuyerName = Value(buyer, "BuyerName"),
            BuyerTaxId = Value(buyer, "BuyerIdNum", "BuyerTaxId"),
            SellerName = Value(seller, "SellerName"),
            SellerTaxId = Value(seller, "SellerIdNum", "SellerTaxId"),
            Amount = Number(basic, true, "TotalAmWithoutTax", "TotalAmountWithoutTax", "TotalAmtWithoutTax"),
            Tax = Number(basic, true, "TotalTaxAm", "TotalTaxAmount"),
            Total = Number(basic, true, "TotalTax-includedAmount", "TotalTaxIncludedAmount"),
            Remarks = Value(additional, "Remark", "Remarks")
        };
        if (invoice.Number != null && !Regex.IsMatch(invoice.Number, @"^[0-9]{20}$"))
        {
            issues.Add("XML 数电发票号码不是 20 位数字，原值保留在 StructuredFields。");
            invoice.Number = null;
        }

        string time = Value(tax, "IssueTime");
        if (time != null)
        {
            if (DateOnly.TryParseExact(time, ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                invoice.IssueDate = date;
            }
            else if (DateTimeOffset.TryParseExact(time, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset timestamp))
            {
                invoice.IssueDate = DateOnly.FromDateTime(timestamp.DateTime);
            }
            else
            {
                issues.Add("XML IssueTime 日期格式无效。");
            }
        }

        XElement[] items = Children(data, "IssuItemInformation", "IssueItemInformation").ToArray();
        if (items.Length > 10000)
        {
            throw new InvalidDataException("XML item limit exceeded.");
        }

        foreach (XElement item in items)
        {
            ct.ThrowIfCancellationRequested();
            string name = Value(item, "ItemName");
            if (name == null)
            {
                issues.Add("XML 明细缺少 ItemName。");
            }

            string rate = Value(item, "TaxRate");
            if (rate != null && decimal.TryParse(rate, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal fractionalRate))
            {
                if (CanonicalNumber(rate) != CanonicalNumber(fractionalRate.ToString(CultureInfo.InvariantCulture)))
                {
                    issues.Add("XML 税率精度超出 decimal 范围，保留原值待复核。");
                }
                else if (fractionalRate is >= 0 and <= 1)
                {
                    rate = (fractionalRate * 100).ToString("0.############################", CultureInfo.InvariantCulture) + "%";
                }
                else
                {
                    issues.Add("XML 数字税率不在 0 到 1 之间，保留原值待复核。");
                }
            }

            invoice.Items.Add(new(name ?? "", Value(item, "SpecMod", "Specification"), Value(item, "MeaUnits", "Unit"), Number(item, false, "Quantity"), Number(item, false, "UnPrice", "UnitPrice"), Number(item, true, "Amount"), rate, Number(item, true, "ComTaxAm", "TaxAmount"), 0));
        }

        if (Section(data, "SpecificInformation") is { } specific && (specific.HasElements || !string.IsNullOrWhiteSpace(specific.Value)))
        {
            issues.Add("XML 包含特定业务信息，本版仅映射通用发票模型。");
        }

        InvoiceValidation.Check(invoice, issues);
        issues.Add("XML 结构化字段已解析；数字签名及税务验真未校验，需复核原件。");
        invoice.Warnings = issues.Distinct().ToList();
        return new(invoice, [], invoice.Warnings, fields);
    }

    private static string CanonicalNumber(string value)
    {
        bool negative = value.StartsWith('-');
        string[] parts = value.TrimStart('+', '-').Split('.');
        string whole = parts[0].TrimStart('0');
        if (whole.Length == 0)
        {
            whole = "0";
        }

        string fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        return (negative && (whole != "0" || fraction.Length > 0) ? "-" : "") + whole + (fraction.Length > 0 ? "." + fraction : "");
    }
}
