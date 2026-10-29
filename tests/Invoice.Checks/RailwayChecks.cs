using System.IO.Compression;
using System.Text;
using Mitzh.Invoice.Core;

internal static class RailwayChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        string path = Path.Combine(root, "railway.ofd");
        void Create(string extra = "", string context = "c")
        {
            using FileStream file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            void Part(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(xml);
            }

            Part("OFD.xml", "<OFD xmlns='http://www.ofdspec.org/2016'><DocBody><DocRoot>D/Document.xml</DocRoot></DocBody></OFD>");
            Part("D/Document.xml", "<Document xmlns='http://www.ofdspec.org/2016'><Pages><Page BaseLoc='p.xml'/></Pages><Attachments>A/Attachments.xml</Attachments></Document>");
            Part("D/A/Attachments.xml", "<Attachments xmlns='http://www.ofdspec.org/2016'><Attachment Format='xml'><FileLoc>rail.xml</FileLoc></Attachment></Attachments>");
            Part("D/A/rail.xml", $"<xbrl xmlns='http://www.xbrl.org/2003/instance' xmlns:r='http://xbrl.mof.gov.cn/taxonomy/2021-11-30/rai'><context id='c'/><r:TypeOfVoucher contextRef='c'>电子发票（铁路电子客票）</r:TypeOfVoucher><r:ElectronicInvoiceRailwayETicketNumber contextRef='{context}'>25000000000000000001</r:ElectronicInvoiceRailwayETicketNumber><r:DateOfIssue contextRef='c'>2025-04-21</r:DateOfIssue><r:TravelDate contextRef='c'>2025-04-13</r:TravelDate><r:Name contextRef='c'>测试旅客</r:Name><r:IdNumber contextRef='c'>123456****1234</r:IdNumber><r:Fare contextRef='c'>109</r:Fare><r:TotalAmountExcludingTax contextRef='c'>100</r:TotalAmountExcludingTax><r:TaxAmount contextRef='c'>9</r:TaxAmount>{extra}</xbrl>");
        }

        using var recognizer = new InvoiceRecognizer();
        Create();
        ParseResult result = await recognizer.RecognizeDetailedAsync(path);
        check(result.Invoice.Number == "25000000000000000001" && result.Invoice.IssueDate == new DateOnly(2025, 4, 21), "Railway attachment maps invoice identity and issue date");
        check(result.Invoice.RailwayTicket?.TravelDate == new DateOnly(2025, 4, 13) && result.Invoice.RailwayTicket.IdNumber == "123456****1234" && result.Invoice.BuyerName == null, "Railway passenger stays separate from purchaser and masked ID is preserved");
        check(result.Invoice.Total == 109 && result.Invoice.Amount == 100 && result.StructuredFields.Count > 0 && result.Invoice.RequiresReview, "Railway totals and unverified structured evidence");
        using (FileStream stream = File.OpenRead(path))
        {
            check((await recognizer.RecognizeAsync(stream)).RailwayTicket == result.Invoice.RailwayTicket && stream.CanRead, "Railway stream API matches path and leaves stream open");
        }

        Create("<r:Fare contextRef='c'>999</r:Fare>");
        try
        {
            await recognizer.RecognizeAsync(path);
            check(false, "Duplicate railway facts rejected");
        }
        catch (InvalidDataException)
        {
            check(true, "Duplicate railway facts rejected");
        }

        Create(context: "other");
        try
        {
            await recognizer.RecognizeAsync(path);
            check(false, "Mixed railway contexts rejected");
        }
        catch (InvalidDataException)
        {
            check(true, "Mixed railway contexts rejected");
        }
    }
}
