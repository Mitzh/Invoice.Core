using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mitzh.Invoice.Core;

internal static class XmlChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "einvoice.xml"));
        using var recognizer = new InvoiceRecognizer(new InvoiceRecognitionOptions { TempDirectory = Path.Combine(root, "xml-temp") });
        ParseResult detailed = await recognizer.RecognizeXmlDetailedAsync(xml);
        InvoiceModel m = detailed.Invoice;
        check(m.Number == "26000000000000000001" && m.Type == "电子发票（增值税专用发票）", "XML content maps invoice identity/type without OCR");
        check(m.BuyerName == "合成测试购买方" && m.BuyerTaxId == "914401010000000001" && m.SellerName == "合成测试销售方" && m.SellerTaxId == "914401010000000002", "XML buyer and seller remain in their own sections");
        check(m.Amount == 300m && m.Tax == 32m && m.Total == 332m && m.Items.Count == 2, "XML totals and repeated sibling items");
        check(m.Items[1].Specification == "A130" && m.Items[1].UnitPrice == 66.6666666666667m && m.Items[1].Quantity == 3 && m.Items[0].TaxRate == "6%" && m.Items[1].TaxRate == "13%", "XML preserves alphanumeric specifications and decimal precision");
        check(m.IssueDate == new DateOnly(2026, 10, 26), "XML IssueTime takes precedence over RequestTime");
        check(m.Drawer == "测试开票人" && detailed.StructuredFields.Any(f => f.Path.Contains("Drawer") && f.Value == m.Drawer), "XML BasicInformation Drawer maps with field evidence");
        check(m.Remarks == "合成测试 & XML内容 <不用于报销>", "XML CDATA/entities are decoded as text");
        check(detailed.Evidence.Count == 0 && detailed.StructuredFields?.Any(f => f.Path.Contains("IssuItemInformation[2]") && f.Value == "A130") == true && m.Items.All(i => i.Page == 0), "XML exposes structured field evidence without invented page coordinates");
        check(m.RequiresReview && m.Warnings.Count == 1 && m.Warnings[0].Contains("数字签名"), "XML signature presence never implies verification");
        async Task<InvoiceModel> Change(Action<XElement> edit)
        {
            var doc = XDocument.Parse(xml);
            edit(doc.Root);
            return await recognizer.RecognizeXmlAsync(doc.ToString());
        }

        XElement Find(XElement e, string name) => e.DescendantsAndSelf().First(n => n.Name.LocalName == name);
        var namespaces = XDocument.Parse(xml);
        foreach (XElement e in namespaces.Descendants())
        {
            e.Name = XName.Get(e.Name.LocalName, "urn:demo:einvoice");
        }

        check((await recognizer.RecognizeXmlAsync(namespaces.ToString())).Items.Count == 2, "XML default namespaces supported");
        namespaces.Root.Add(new XAttribute(XNamespace.Xmlns + "e", "urn:demo:einvoice"));
        check((await recognizer.RecognizeXmlAsync(namespaces.ToString())).BuyerName == m.BuyerName, "XML namespace prefix does not change mapping");
        check((await recognizer.RecognizeXmlAsync(xml.Replace("encoding=\"utf-8\"", "encoding=\"utf-16\""))).SellerName == m.SellerName, "decoded XML strings are not reinterpreted using byte encoding declarations");
        foreach ((string encodingName, Encoding encoding) in new (string, Encoding)[]
        {
            ("utf-8", new UTF8Encoding(true)),
            ("utf-16", Encoding.Unicode),
            ("utf-16BE", Encoding.BigEndianUnicode),
            ("utf-32", Encoding.UTF32),
            ("utf-32BE", new UTF32Encoding(true, true)),
            ("gb18030", Encoding.GetEncoding("gb18030"))
        }

        )
        {
            string path = Path.Combine(root, "sample-" + encodingName + ".xml");
            string content = xml.Replace("encoding=\"utf-8\"", "encoding=\"" + encodingName + "\"");
            byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
            File.WriteAllBytes(path, bytes);
            check((await recognizer.RecognizeAsync(path)).BuyerName == m.BuyerName, "XML file encoding: " + encodingName);
        }

        using (var stream = new NonSeekableShortStream(Encoding.UTF8.GetBytes(xml)))
        {
            check((await recognizer.RecognizeAsync(stream)).Total == 332m && stream.CanRead, "XML supports non-seekable short-read streams and leaves them open");
        }

        string whitespaceXml = new string(' ', 1024) + XDocument.Parse(xml).ToString();
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(whitespaceXml)))
        {
            check((await recognizer.RecognizeAsync(stream)).Number == m.Number, "XML stream detection accepts long leading whitespace without declaration");
        }

        InvoiceModel leadingZero = await Change(e => Find(e, "InvoiceNumber").Value = "00000000000000000001");
        check(leadingZero.Number == "00000000000000000001", "XML invoice numbers preserve leading zeros");
        InvoiceModel noNumber = await Change(e => Find(e, "InvoiceNumber").Remove());
        check(noNumber.Number == null, "XML EIid is not silently substituted for missing InvoiceNumber");
        InvoiceModel noDate = await Change(e => Find(e, "IssueTime").Remove());
        check(noDate.IssueDate == null, "XML RequestTime is not substituted for a missing tax issue date");
        InvoiceModel offsetDate = await Change(e => Find(e, "IssueTime").Value = "2026-10-26T00:05:00+08:00");
        check(offsetDate.IssueDate == new DateOnly(2026, 10, 26), "XML timestamp date preserves written offset date without UTC day shift");
        InvoiceModel precise = await Change(e => Find(e, "UnPrice").Value = "0.12345678901234567890123456789");
        check(precise.Items[0].UnitPrice == null && precise.Warnings.Any(w => w.Contains("数值字段")), "XML decimal precision loss is rejected instead of silently rounded");
        InvoiceModel overflow = await Change(e =>
        {
            Find(e, "UnPrice").Value = decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Find(e, "Quantity").Value = "2";
        });
        check(overflow.Warnings.Any(w => w.Contains("超出 decimal")), "XML arithmetic overflow becomes a review issue");
        InvoiceModel red = await Change(e =>
        {
            foreach (XElement v in e.Descendants().Where(n => new[] { "TotalAmWithoutTax", "TotalTaxAm", "TotalTax-includedAmount", "UnPrice", "Amount", "ComTaxAm" }.Contains(n.Name.LocalName)))
            {
                v.Value = "-" + v.Value;
            }
        });
        check(red.Total == -332m && red.Items[1].Amount == -200m && !red.Warnings.Any(w => w.Contains("不一致")), "XML signed amounts are preserved without assuming tax red-invoice status");
        InvoiceModel invalid = await Change(e => Find(e, "TotalAmWithoutTax").Value = "bad-number");
        check(invalid.Amount == null && invalid.Warnings.Any(w => w.Contains("数值字段")), "invalid XML numeric values become null with a warning, not zero");
        InvoiceModel zero = await Change(e =>
        {
            Find(e, "Quantity").Value = "0";
            Find(e, "SpecMod").Value = "";
        });
        check(zero.Items[0].Quantity == 0 && zero.Items[0].Specification == null, "XML zero and absent optional values remain distinct");
        InvoiceModel exempt = await Change(e => Find(e, "TaxRate").Value = "免税");
        check(exempt.Items[0].TaxRate == "免税", "XML nonnumeric tax semantics are retained");
        InvoiceModel preciseRate = await Change(e => Find(e, "TaxRate").Value = "0.12345678901234567890123456789");
        check(preciseRate.Items[0].TaxRate == "0.12345678901234567890123456789" && preciseRate.Warnings.Any(w => w.Contains("税率精度")), "XML unrepresentable tax rates are preserved rather than rounded");
        InvoiceModel special = await Change(e => Find(e, "SpecificInformation").Value = "行业扩展");
        check(special.Warnings.Any(w => w.Contains("特定业务信息")), "XML industry-specific content is explicitly flagged as unmapped");
        InvoiceModel conflict = await Change(e => Find(e, "BasicInformation").Add(new XElement("TotalAmountWithoutTax", "301.00")));
        check(conflict.Amount == null && conflict.Warnings.Any(w => w.Contains("冲突")), "conflicting XML aliases do not pick a value silently");
        InvoiceModel mismatch = await Change(e => Find(e, "TotalTax-includedAmount").Value = "333.00");
        check(mismatch.Warnings.Any(w => w.Contains("金额加税额")), "XML uses shared financial consistency checks");
        InvoiceModel wrongNamespace = await Change(e =>
        {
            Find(e, "InvoiceNumber").Remove();
            Find(e, "TaxSupervisionInfo").Add(new XElement(XName.Get("InvoiceNumber", "urn:foreign"), "26000000000000000009"));
        });
        check(wrongNamespace.Number == null, "foreign-namespace nodes do not impersonate tax fields");
        async Task Reject(string payload, string label)
        {
            try
            {
                await recognizer.RecognizeXmlAsync(payload);
                check(false, label);
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException)
            {
                check(true, label);
            }
        }

        await Reject("<!DOCTYPE EInvoice [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><EInvoice><EInvoiceData>&x;</EInvoiceData></EInvoice>", "XML DTD/external entities prohibited");
        await Reject("<Invoice><InvoiceNumber>26000000000000000001</InvoiceNumber></Invoice>", "unrecognized XML schema rejected instead of guessed");
        await Reject("<EInvoice><EInvoiceData></EInvoice>", "malformed XML rejected");
        await Reject("<EInvoice>" + string.Concat(Enumerable.Repeat("<x>", 66)) + string.Concat(Enumerable.Repeat("</x>", 66)) + "</EInvoice>", "XML depth limit enforced before tree construction");
        await Reject(xml.Replace("</BasicInformation>", "</BasicInformation><BasicInformation/>"), "duplicate XML sections rejected");
        await Reject("<EInvoice><EInvoiceData>" + string.Concat(Enumerable.Repeat("<x/>", 100001)) + "</EInvoiceData></EInvoice>", "XML node limit enforced");
        using (var small = new InvoiceRecognizer(new InvoiceRecognitionOptions { MaxBytes = 32, TempDirectory = Path.Combine(root, "xml-small-temp") }))
        {
            try
            {
                await small.RecognizeXmlAsync(xml);
                check(false, "XML content byte limit");
            }
            catch (InvalidDataException)
            {
                check(true, "XML content byte limit");
            }
        }

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await recognizer.RecognizeXmlAsync(xml, cancelled.Token);
                check(false, "XML cancellation");
            }
            catch (OperationCanceledException)
            {
                check(true, "XML cancellation");
            }
        }
    }
}
