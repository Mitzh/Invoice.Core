using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mitzh.Invoice.Core;

internal static class RailwayOfdReader
{
    private static readonly XNamespace Ofd = "http://www.ofdspec.org/2016";
    private static readonly XNamespace Rai = "http://xbrl.mof.gov.cn/taxonomy/2021-11-30/rai";
    private static readonly XNamespace Xbrl = "http://www.xbrl.org/2003/instance";
    public static ParseResult Read(string path, int maxPages, CancellationToken ct)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 4000 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024 || zip.Entries.GroupBy(e => e.FullName).Any(g => g.Count() > 1))
        {
            throw new InvalidDataException("Invalid or oversized OFD archive.");
        }

        XDocument Load(string name)
        {
            ct.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = zip.GetEntry(name) ?? throw new InvalidDataException("Missing OFD part: " + name);
            if (entry.Length > 1024 * 1024)
            {
                throw new InvalidDataException("OFD metadata part too large.");
            }

            using Stream input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var document = XDocument.Load(reader);
            if (document.Descendants().Any(e => e.Ancestors().Take(65).Count() > 64))
            {
                throw new InvalidDataException("OFD metadata depth limit exceeded.");
            }

            return document;
        }

        XElement[] roots = Load("OFD.xml").Descendants(Ofd + "DocRoot").ToArray();
        if (roots.Length == 0)
        {
            return null;
        }

        if (roots.Length != 1)
        {
            throw new InvalidDataException("Only single-document OFD packages are supported.");
        }

        string docPath = OfdReader.Resolve("", roots[0].Value);
        XDocument doc = Load(docPath);
        int count = doc.Descendants(Ofd + "Page").Count();
        if (count == 0 || count > maxPages)
        {
            throw new InvalidDataException("Invalid OFD page count.");
        }

        XElement[] manifests = doc.Root.Elements(Ofd + "Attachments").ToArray();
        if (manifests.Length == 0)
        {
            return null;
        }

        if (manifests.Length != 1)
        {
            throw new InvalidDataException("Ambiguous OFD attachments.");
        }

        string manifestPath = OfdReader.Resolve(docPath, manifests[0].Value);
        var candidates = new List<(string Path, XElement Root)>();
        foreach (XElement attachment in Load(manifestPath).Root.Elements(Ofd + "Attachment"))
        {
            if (!string.Equals((string)attachment.Attribute("Format"), "xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            XElement[] locations = attachment.Elements(Ofd + "FileLoc").ToArray();
            if (locations.Length != 1)
            {
                throw new InvalidDataException("Invalid attachment location.");
            }

            string part = OfdReader.Resolve(manifestPath, locations[0].Value);
            XElement root = Load(part).Root;
            if (root.Name == Xbrl + "xbrl" && root.Elements(Rai + "ElectronicInvoiceRailwayETicketNumber").Any())
            {
                candidates.Add((part, root));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count != 1)
        {
            throw new InvalidDataException("Multiple railway invoices in OFD; split the package first.");
        }

        (string Path, XElement Root) selected = candidates[0];
        XElement[] facts = selected.Root.Elements().Where(e => e.Name.Namespace == Rai).ToArray();
        string[] contexts = facts.Select(e => (string)e.Attribute("contextRef")).Distinct().ToArray();
        if (contexts.Length != 1 || string.IsNullOrWhiteSpace(contexts[0]) || selected.Root.Elements(Xbrl + "context").Count(e => (string)e.Attribute("id") == contexts[0]) != 1)
        {
            throw new InvalidDataException("Ambiguous railway XBRL context.");
        }

        var issues = new List<string>();
        string Value(string name)
        {
            XElement[] matches = facts.Where(e => e.Name.LocalName == name).ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidDataException("Duplicate railway fact: " + name);
            }

            XElement element = matches.SingleOrDefault();
            if (element == null || (string)element.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "nil") is "true" or "1")
            {
                return null;
            }

            if (element.HasElements || element.Value.Length > 4096)
            {
                throw new InvalidDataException("Invalid railway fact: " + name);
            }

            return string.IsNullOrWhiteSpace(element.Value) ? null : element.Value.Trim();
        }

        decimal? Money(string name)
        {
            string value = Value(name);
            if (value == null)
            {
                return null;
            }

            if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount) && value.Length <= 28)
            {
                return amount;
            }

            issues.Add("铁路金额字段无效：" + name);
            return null;
        }

        DateOnly? Date(string name)
        {
            string value = Value(name);
            if (value == null)
            {
                return null;
            }

            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                return date;
            }

            issues.Add("铁路日期字段无效：" + name);
            return null;
        }

        string number = Value("ElectronicInvoiceRailwayETicketNumber");
        if (number == null || !Regex.IsMatch(number, "^[0-9]{20}$"))
        {
            throw new InvalidDataException("Invalid railway invoice number.");
        }

        string type = Value("TypeOfVoucher");
        if (type != "电子发票（铁路电子客票）")
        {
            throw new InvalidDataException("Unsupported railway voucher type.");
        }

        var invoice = new InvoiceModel
        {
            Type = type,
            Number = number,
            IssueDate = Date("DateOfIssue"),
            BuyerName = Value("NameOfPurchaser"),
            BuyerTaxId = Value("UnifiedSocialCreditCodeOfPurchaser"),
            SellerName = Value("IssueParty"),
            SellerTaxId = Value("IssuePartyCode"),
            Amount = Money("TotalAmountExcludingTax"),
            Tax = Money("TaxAmount"),
            Total = Money("Fare"),
            Remarks = Value("Remarks"),
            RailwayTicket = new(Value("Name"), Value("IdNumber"), Value("TrainNumber"), Value("DepartureStation"), Value("DestinationStation"), Date("TravelDate"), Value("DepartureTime"), Value("SeatLevel"), Value("Carriage"), Value("Seat"), Value("ETicketNumber"), Value("TypeOfBusiness"))
        };
        if (invoice.IssueDate == null)
        {
            issues.Add("缺少有效开票日期。");
        }

        if (invoice.Amount == null || invoice.Tax == null || invoice.Total == null)
        {
            issues.Add("金额汇总不完整。");
        }
        else if (Math.Abs(invoice.Total.Value - invoice.Amount.Value - invoice.Tax.Value) > .01m)
        {
            issues.Add("金额加税额与票价不一致。");
        }

        issues.Add("已读取 OFD 内嵌铁路客票 XML；未验证数字签名或税务真实性，请复核原件。");
        invoice.Warnings = issues;
        StructuredFieldEvidence[] evidence = facts.Select(e => new StructuredFieldEvidence(selected.Path + "/rai:" + e.Name.LocalName, e.Value, "ofd-railway-xbrl")).ToArray();
        return new(invoice, [], issues, evidence);
    }
}
