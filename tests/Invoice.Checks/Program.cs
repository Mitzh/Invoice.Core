using System.IO.Compression;
using System.Xml.Linq;
using Mitzh.Invoice.Core;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

string root = Path.GetFullPath(args.FirstOrDefault() ?? "work/check-fixtures");
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool ok, string label)
{
    if (!ok)
    {
        throw new Exception("FAIL: " + label);
    }

    Console.WriteLine("PASS: " + label);
    passed++;
}

var typeBoxes = new List<TextBox>
{
    new(1, 100, 20, 100, 30, "电子发票", "rapidocr"),
    new(1, 205, 20, 110, 30, "曾值税专", "rapidocr"),
    new(1, 320, 20, 100, 30, "用发票）", "rapidocr")
};
Check(InvoiceMapper.Map(typeBoxes).Invoice.Type == InvoiceTypeReader.Special, "split title fragments resolve explicitly read special-invoice words");
Check(InvoiceMapper.Map([new(1, 100, 20, 100, 30, "电 子 发 票 ( 普 通 发 票 )", "rapidocr")]).Invoice.Type == InvoiceTypeReader.Ordinary, "ordinary electronic title normalizes whitespace and parentheses");
Check(InvoiceMapper.Map([new(1, 100, 20, 100, 30, "电子发票（增值税普通发票）", "rapidocr")]).Invoice.Type == InvoiceTypeReader.Ordinary, "electronic ordinary title alias has a canonical display name");
ParseResult partialTitle = InvoiceMapper.Map([new(1, 100, 20, 100, 30, "电子发票", "rapidocr")]);
Check(partialTitle.Invoice.Type == "电子发票" && partialTitle.Issues.Any(s => s.Contains("类型尚未确认")), "generic electronic title does not guess special versus ordinary");
Check(InvoiceMapper.Map(typeBoxes.Take(1).Append(new TextBox(1, 100, 200, 300, 10, "备注：请开具增值税专用发票", "rapidocr")).ToList()).Invoice.Type == "电子发票", "body remarks cannot supply the title's special-invoice qualifier");
Check(InvoiceMapper.Map([new(1, 100, 20, 300, 30, "电子发票（增值税专用发票）", "rapidocr"), new(1, 100, 60, 300, 30, "电子发票（普通发票）", "rapidocr")]).Issues.Any(s => s.Contains("同时存在专票与普票")), "conflicting explicit invoice titles remain unresolved");
Check(InvoiceMapper.Map([new(1, 0, 0, 300, 30, "增值税电子普通发票", "pdf-text")]).Invoice.Type == "增值税电子普通发票", "legacy electronic title is not relabelled as a digital electronic invoice");
Check(InvoiceMapper.Map([new(1, 0, 0, 300, 30, "电子发票（铁路电子客票）", "pdf-text")]).Invoice.Type == "电子发票（铁路电子客票）", "explicit other invoice categories are retained");
Check(InvoiceMapper.Map([new(1, 100, 20, 50, 30, "电子", "rapidocr"), new(1, 155, 20, 250, 30, "发票（增值税专用发票）", "rapidocr")]).Invoice.Type == InvoiceTypeReader.Special, "electronic title prefix can be split across adjacent text boxes");
Check(InvoiceMapper.Map([new(1, 100, 20, 100, 30, "电子发票", "rapidocr"), new(1, 205, 20, 100, 30, "专用发票", "rapidocr"), new(1, 310, 20, 100, 30, "普通发票", "rapidocr")]).Invoice.Type == "电子发票", "one title band with contradictory qualifiers never picks a category");
Check(InvoiceMapper.Map([new(1, 0, 0, 100, 10, "开票人：测试甲", "rapidocr")]).Invoice.Drawer == "测试甲", "OCR complete drawer label maps to model");
Check(InvoiceMapper.Map([new(1, 0, 0, 20, 7, "开票", "pdf-text"), new(1, 23, 0, 12, 7, "人：", "pdf-text"), new(1, 34, 0, 20, 7, "测试乙", "pdf-text")]).Invoice.Drawer == "测试乙", "PDF split and overlapping drawer labels are joined");
Check(InvoiceMapper.Map([new(1, 0, 0, 200, 10, "收款人：甲 复核：乙 开票人：丙", "rapidocr")]).Invoice.Drawer == "丙", "combined footer roles select the explicitly labelled drawer");
Check(InvoiceMapper.Map([new(1, 0, 0, 200, 10, "开票人：测试丙 复核：测试丁", "rapidocr")]).Invoice.Drawer == "测试丙", "drawer excludes trailing reviewer field");
Check(InvoiceMapper.Map([new(1, 0, 0, 100, 10, "收款人：测试甲", "rapidocr"), new(1, 0, 20, 100, 10, "复核：测试乙", "rapidocr")]).Invoice.Drawer == null, "cashier and reviewer are not drawer aliases");
Check(InvoiceMapper.Map([new(1, 0, 0, 100, 10, "开票人：测试甲", "rapidocr"), new(2, 0, 0, 100, 10, "开票人：测试乙", "rapidocr")]).Issues.Any(s => s.Contains("多个不同开票人")), "conflicting drawer names remain unresolved");
Check(InvoiceMapper.Map([new(1, 0, 0, 100, 10, "开票员：测试甲", "ofd-text")]).Invoice.Drawer == "测试甲", "OFD drawer alias is supported");
Check(InvoiceMapper.Map([new(1, 0, 0, 200, 10, "备注：请联系开票人：测试甲", "rapidocr")]).Invoice.Drawer == null, "mentions inside remarks are not treated as a drawer field");
PdfTextSpan[] nativeSpans = new[]
{
    new PdfTextSpan(1, 100, 10, 390, 20, 30.1, 20, "电子发票（增值税专用发票）"),
    new PdfTextSpan(2, 400, 22, 10, 8, 30, 9, "发"),
    new PdfTextSpan(3, 413, 22, 10, 8, 30, 9, "票"),
    new PdfTextSpan(4, 426, 22, 10, 8, 30, 9, "号"),
    new PdfTextSpan(5, 439, 22, 10, 8, 30, 9, "码"),
    new PdfTextSpan(6, 452, 25, 2, 5, 30, 9, "："),
    new PdfTextSpan(80, 450, 23, 90, 7, 29.8, 8.8, "12345678901234567890"),
    new PdfTextSpan(7, 400, 42, 50, 8, 50, 9, "开票日期："),
    new PdfTextSpan(81, 448, 43, 90, 7, 50.1, 8.8, "2026年10月26日")
};
List<TextBox> headerLines = PdfHeaderRecovery.Rebuild(nativeSpans.Reverse(), 1);
InvoiceModel headerModel = InvoiceMapper.Map(headerLines).Invoice;
Check(headerLines.Count == 3 && headerModel.Type == "电子发票（增值税专用发票）", "PDF header font sizes keep title separate from invoice number");
Check(headerModel.Number == "12345678901234567890", "PDF drawing sequence preserves digits when number overlaps label punctuation");
Check(headerModel.IssueDate == new DateOnly(2026, 10, 26), "PDF native date characters are not reordered by nearest-neighbour distance");
Check(headerLines.All(b => b.Source == "pdf-text-header" && b.Confidence == null), "PDF header reconstruction stays native and does not invent OCR confidence");
var boxes = new List<TextBox>();
void Add(int p, double x, double y, string text) => boxes.Add(new(p, x, y, 20, 3, text, "fixture"));
Add(1, 10, 10, "电子发票（普通发票）");
Add(1, 10, 20, "发票号码：12345678901234567890");
Add(1, 10, 30, "开票日期：2026年10月23日");
Add(1, 10, 40, "购买方名称：测试采购公司");
Add(1, 10, 50, "购买方税号：914401010000000001");
Add(1, 10, 60, "销售方名称：测试销售公司");
Add(1, 10, 70, "销售方税号：914401010000000002");
string[] heads = ["项目名称", "规格型号", "单位", "数量", "单价", "金额", "税率", "税额"];
for (int p = 1; p <= 2; p++)
{
    for (int c = 0; c < 8; c++)
    {
        Add(p, 10 + c * 25, 80, heads[c]);
    }

    string[] cells = ["*服务*测试服务" + p, "V1", "次", "1", "100.00", "100.00", "6%", "6.00"];
    for (int c = 0; c < 8; c++)
    {
        Add(p, 10 + c * 25, 90, cells[c]);
    }

    Add(p, 10, 100, "合计");
}

Add(2, 10, 110, "不含税金额：200.00");
Add(2, 10, 120, "税额合计：12.00");
Add(2, 10, 130, "价税合计（小写）：212.00");
ParseResult parsed = InvoiceMapper.Map(boxes);
Check(parsed.Invoice.Items.Count == 2 && parsed.Invoice.Items[1].Page == 2, "multi-page rows and repeated headers");
Check(parsed.Invoice.Number == "12345678901234567890" && parsed.Invoice.Code == null, "digital invoice identity");
Check(parsed.Invoice.IssueDate == new DateOnly(2026, 10, 23) && parsed.Invoice.Total == 212m, "date and monetary fields");
Check(parsed.Issues.Count == 1 && parsed.NeedsReview, "unaccepted template always requires review");
// Electronic invoice layout regression: vertical party labels, split headers, blank cells and wrapped item names.
var layout = boxes.Where(b => !b.Text.StartsWith("购买方") && !b.Text.StartsWith("销售方")).SelectMany(b => new[] { "单位", "数量", "单价", "金额", "税额" }.Contains(b.Text) ? new[] { b with { Text = b.Text[..1], Width = 7 }, b with { Text = b.Text[1..], X = b.X + 10, Width = 7 } } : new[] { b.Text == "税率" ? b with { Text = "税率/征收率" } : b }).ToList();
foreach ((string role, double x, string name, string taxId) in new[]
{
    ("购买方", 0d, "测试采购主体", "914401010000000001"),
    ("销售方", 120d, "测试销售主体", "914401010000000002")
}

)
{
    for (int j = 0; j < role.Length; j++)
    {
        layout.Add(new(1, x, 40 + j * 10, 3, 3, role[j].ToString(), "fixture"));
    }

    layout.Add(new(1, x + 10, 40, 20, 3, "名称：", "fixture"));
    layout.Add(new(1, x + 40, 40, 30, 3, name, "fixture"));
    layout.Add(new(1, x + 10, 65, 30, 3, "统一社会信用代码/纳税人识别号：", "fixture"));
    layout.Add(new(1, x + 50, 65, 30, 3, taxId, "fixture"));
}

layout.Add(new(1, 10, 95, 20, 3, "续行文本", "fixture"));
ParseResult spatial = InvoiceMapper.Map(layout);
Check(spatial.Invoice.BuyerName == "测试采购主体" && spatial.Invoice.SellerName == "测试销售主体" && spatial.Invoice.BuyerTaxId == "914401010000000001" && spatial.Invoice.SellerTaxId == "914401010000000002", "vertical parties separated into left/right regions");
Check(spatial.Invoice.Items.Count == 2 && spatial.Invoice.Items[0].Name.EndsWith("续行文本"), "split headers and wrapped item name");
var marginLayout = layout.Where(b => b.Text != "名称：" && b.Text != "测试采购主体" && b.Text != "测试销售主体").ToList();
marginLayout.Add(new(1, 10, 40, 70, 3, "名称：测试采购主体", "fixture"));
marginLayout.Add(new(1, 130, 40, 70, 3, "名称：测试销售主体", "fixture"));
marginLayout.Add(new(1, 241, 40, 3, 25, "下载次数：1", "fixture"));
InvoiceModel marginModel = InvoiceMapper.Map(marginLayout).Invoice;
Check(marginModel.BuyerName == "测试采购主体" && marginModel.SellerName == "测试销售主体", "tall page-margin annotation does not contaminate party fields");
var separatedMargin = layout.Append(new TextBox(1, 241, 40, 3, 25, "下载次数：1", "fixture")).ToList();
Check(InvoiceMapper.Map(separatedMargin).Invoice.SellerName == "测试销售主体", "split name label and value remain supported with marginal text");
ParseResult smallCase = InvoiceMapper.Map(boxes.Select(b => b.Text.Contains("212.00") ? b with { Text = "价税合计（大写）贰佰壹拾贰圆整（小写）¥212.00" } : b).ToList());
Check(smallCase.Invoice.Total == 212m, "parenthesized small-case total label");
var shortNumber = new TextBox(1, 10, 10, 100, 10, "发票号码：1234567890123456789", "rapidocr");
Check(InvoiceMapper.Map([shortNumber]).Invoice.Number == null, "nineteen digits are never padded into an invoice number");
TextBox recoveredNumber = shortNumber with
{
    Text = "发票号码：12345678901234567890",
    Source = "rapidocr-number-retry"
};
InvoiceModel recoveredModel = InvoiceMapper.Map([shortNumber, recoveredNumber]).Invoice;
Check(recoveredModel.Number == "12345678901234567890" && recoveredModel.Warnings.Any(w => w.Contains("局部放大")), "region OCR evidence restores a valid number with a review warning");
Check(InvoiceMapper.Map([recoveredNumber, recoveredNumber with { Text = "发票号码：22345678901234567890" }]).Invoice.Number == null, "conflicting valid number candidates require review");
Check(InvoiceMapper.Map([shortNumber with { Text = "发票号码：１２３４５６７８９０１２３４５６７８９０" }]).Invoice.Number == "12345678901234567890", "fullwidth digits normalized without guessing");
ParseResult watermarkTotal = InvoiceMapper.Map([new(1, 10, 10, 90, 10, "（小写）￥123.45", "rapidocr"), new(1, 110, 10, 50, 40, "2026", "rapidocr")]);
Check(watermarkTotal.Invoice.Total == 123.45m, "watermark digits in the same geometric row never extend a labelled total");
Check(InvoiceMapper.Map([new(1, 10, 10, 100, 10, "（小写）￥123.452026", "rapidocr")]).Invoice.Total == null, "contaminated decimal total is rejected instead of silently truncated");
Check(InvoiceMapper.Map([new(1, 10, 10, 100, 10, "开票日期：2026年10月231", "rapidocr")]).Invoice.IssueDate == null, "OCR date with extra trailing digits is rejected");
ParseResult diagnostics = InvoiceMapper.Map([shortNumber with { Text = "发票号码：22345678901234567890", DiagnosticOnly = true }, recoveredNumber]);
Check(diagnostics.Invoice.Number == "12345678901234567890" && diagnostics.Evidence.Count == 2, "superseded OCR evidence is retained without contaminating active fields");
ParseResult currencySummary = InvoiceMapper.Map([new(1, 0, 0, 10, 5, "合计", "rapidocr-enhanced"), new(1, 30, 0, 30, 5, "Y100.00", "rapidocr-enhanced"), new(1, 70, 0, 30, 5, "Y13.00", "rapidocr-enhanced")]);
Check(currencySummary.Invoice.Amount == 100m && currencySummary.Invoice.Tax == 13m && InvoiceMapper.Money("Y100.00") == null, "currency Y is normalized only in an identified summary row");
ParseResult mismatch = InvoiceMapper.Map(boxes.Select(b => b.Text.Contains("212.00") ? b with { Text = "价税合计（小写）：222.00" } : b).ToList());
Check(mismatch.Issues.Any(s => s.Contains("金额加税额")), "amount inconsistency");
ParseResult missing = InvoiceMapper.Map([new(1, 0, 0, 1, 1, "发票号码：12345678", "test")]);
Check(missing.Issues.Any(s => s.Contains("发票代码")), "traditional invoice requires code");
XNamespace ns = "http://www.ofdspec.org/2016";
void Zip(string path, Dictionary<string, string> files)
{
    using ZipArchive z = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (KeyValuePair<string, string> f in files)
    {
        using var w = new StreamWriter(z.CreateEntry(f.Key).Open());
        w.Write(f.Value);
    }
}

string PageXml(int p) => new XElement(ns + "Page", new XElement(ns + "Content", boxes.Where(b => b.Page == p).Select(b => new XElement(ns + "TextObject", new XAttribute("Boundary", $"{b.X} {b.Y} {b.Width} {b.Height}"), new XElement(ns + "TextCode", new XAttribute("X", 0), new XAttribute("Y", 0), b.Text))))).ToString();
string ofd = Path.Combine(root, "synthetic.ofd");
if (File.Exists(ofd))
{
    File.Delete(ofd);
}

Zip(ofd, new() { ["OFD.xml"] = "<OFD><DocBody><DocRoot>Doc/Document.xml</DocRoot></DocBody></OFD>", ["Doc/Document.xml"] = "<Document><Pages><Page ID='9' BaseLoc='Pages/Z.xml'/><Page ID='2' BaseLoc='Pages/A.xml'/></Pages></Document>", ["Doc/Pages/Z.xml"] = PageXml(1), ["Doc/Pages/A.xml"] = PageXml(2) });
List<TextBox> ofdBoxes = OfdReader.Read(ofd, 30, default);
Check(InvoiceMapper.Map(ofdBoxes).Invoice.Items.Count == 2 && ofdBoxes[0].Text.Contains("电子发票"), "OFD manifest order differs from filename order");
try
{
    OfdReader.Read(ofd, 1, default);
    Check(false, "OFD page cap");
}
catch (InvalidDataException)
{
    Check(true, "OFD page cap");
}

string malicious = Path.Combine(root, "xxe.ofd");
if (File.Exists(malicious))
{
    File.Delete(malicious);
}

Zip(malicious, new() { ["OFD.xml"] = "<!DOCTYPE a [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><OFD>&e;</OFD>" });
try
{
    OfdReader.Read(malicious, 30, default);
    Check(false, "DTD prohibited");
}
catch (System.Xml.XmlException)
{
    Check(true, "DTD prohibited");
}

using (var bmp = new SKBitmap(1800, 1000))
{
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.White);
    using var font = new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei"), 32);
    using var paint = new SKPaint
    {
        Color = SKColors.Black,
        IsAntialias = true
    };
    int y = 70;
    foreach (string line in new[]
    {
        "电子发票（普通发票）",
        "发票号码：12345678901234567890",
        "开票日期：2026年10月23日",
        "购买方名称：测试采购公司",
        "销售方名称：测试销售公司",
        "不含税金额：200.00",
        "税额合计：12.00",
        "价税合计：212.00"
    }

    )
    {
        canvas.DrawText(line, 60, y, font, paint);
        y += 90;
    }

    using (FileStream fs = File.Create(Path.Combine(root, "synthetic.png")))
    {
        bmp.Encode(fs, SKEncodedImageFormat.Png, 100);
    }

    using (FileStream fs = File.Create(Path.Combine(root, "synthetic.jpg")))
    {
        bmp.Encode(fs, SKEncodedImageFormat.Jpeg, 95);
    }
}

// Text PDF uses standard fonts; Chinese image OCR is tested separately above.
var builder = new PdfDocumentBuilder();
PdfPageBuilder page = builder.AddPage(600, 800);
PdfDocumentBuilder.AddedFont pdfFont = builder.AddStandard14Font(Standard14Font.Helvetica);
page.AddText("Invoice number: 12345678901234567890. Synthetic test document only.", 12, new PdfPoint(20, 700), pdfFont);
File.WriteAllBytes(Path.Combine(root, "text.pdf"), builder.Build());
var scanBuilder = new PdfDocumentBuilder();
PdfPageBuilder scanPage = scanBuilder.AddPage(600, 400);
scanPage.AddPng(File.ReadAllBytes(Path.Combine(root, "synthetic.png")), new PdfRectangle(0, 0, 600, 400));
File.WriteAllBytes(Path.Combine(root, "scan.pdf"), scanBuilder.Build());
var lim = new InvoiceRecognitionOptions();
using (var parser = new DocumentParser(lim))
{
    ParseResult result = await parser.Parse(Path.Combine(root, "text.pdf"), ".pdf", default);
    Check(result.Evidence.All(b => b.Source == "pdf-text"), "native PDF text extraction");
    try
    {
        await parser.Parse(Path.Combine(root, "synthetic.png"), ".png", default);
        Check(false, "unconfigured OCR fails explicitly");
    }
    catch (InvalidOperationException ex) when (ex.Message.StartsWith("OCR_NOT_CONFIGURED"))
    {
        Check(true, "unconfigured OCR fails explicitly");
    }
}

string det = Environment.GetEnvironmentVariable("TEST_OCR_DET");
lim.TempDirectory = Path.Combine(root, "temp");
using (var recognizer = new InvoiceRecognizer(lim))
{
    InvoiceModel byPath = await recognizer.RecognizeAsync(ofd);
    Check(byPath.Number == "12345678901234567890" && byPath.Items.Count == 2, "simple path call returns InvoiceModel");
    using var input = new NonSeekableShortStream(File.ReadAllBytes(ofd));
    InvoiceModel byStream = await recognizer.RecognizeAsync(input);
    Check(byStream.Number == byPath.Number && byStream.Items.Count == byPath.Items.Count, "non-seekable short-read stream matches path result");
    Check(input.CanRead, "caller stream is left open");
    Check(byStream.RequiresReview && byStream.Warnings.Count > 0, "review warnings accompany direct model");
    using var empty = new MemoryStream();
    try
    {
        await recognizer.RecognizeAsync(empty);
        Check(false, "empty stream rejected");
    }
    catch (InvalidDataException)
    {
        Check(true, "empty stream rejected");
    }

    Check(!Directory.EnumerateFileSystemEntries(lim.TempDirectory).Any(), "temporary files removed after success and failure");
}

using (var recognizer = new InvoiceRecognizer(new InvoiceRecognitionOptions { MaxBytes = 16, TempDirectory = lim.TempDirectory }))
{
    using var input = new MemoryStream(File.ReadAllBytes(ofd));
    try
    {
        await recognizer.RecognizeAsync(input);
        Check(false, "stream size cap");
    }
    catch (InvalidDataException)
    {
        Check(true, "stream size cap");
    }
}

if (det != null)
{
    lim.DetModel = det;
    lim.ClsModel = Environment.GetEnvironmentVariable("TEST_OCR_CLS");
    lim.RecModel = Environment.GetEnvironmentVariable("TEST_OCR_REC");
    lim.Dictionary = Environment.GetEnvironmentVariable("TEST_OCR_DICT");
    lim.PdfToPpm = Environment.GetEnvironmentVariable("TEST_PDFTOPPM") ?? "pdftoppm";
    using var recognizer = new InvoiceRecognizer(lim);
    foreach (string file in new[]
    {
        "synthetic.png",
        "synthetic.jpg",
        "scan.pdf"
    }

    )
    {
        ParseResult r = await recognizer.RecognizeDetailedAsync(Path.Combine(root, file));
        File.WriteAllText(Path.Combine(root, file + ".result.json"), System.Text.Json.JsonSerializer.Serialize(r, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Check(r.Evidence.Any(b => b.Source == "rapidocr" && b.Text.Contains("发票")), "real Chinese ONNX inference: " + file);
        Check(r.Invoice.Number == "12345678901234567890" && r.Invoice.Total == 212m, "recognized model fields: " + file);
    }
}
else
{
    Console.WriteLine("SKIP Chinese OCR: configure TEST_OCR_* paths.");
}

await XmlChecks.Run(Check, root);
await RailwayChecks.Run(Check, root);
Console.WriteLine($"{passed} checks passed.");
internal sealed class NonSeekableShortStream(byte[] data) : Stream
{
    private readonly MemoryStream inner = new(data);
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 3));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
