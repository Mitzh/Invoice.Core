namespace Mitzh.Invoice.Core;

public record TextBox(int Page, double X, double Y, double Width, double Height, string Text, string Source, double? Confidence = null, bool DiagnosticOnly = false);
public record InvoiceItemModel(string Name, string Specification, string Unit, decimal? Quantity, decimal? UnitPrice, decimal? Amount, string TaxRate, decimal? Tax, int Page);
public sealed class InvoiceModel
{
    public string Type
    {
        get; set;
    }
    public string Code
    {
        get; set;
    }
    public string Number
    {
        get; set;
    }
    public DateOnly? IssueDate
    {
        get; set;
    }
    public string CheckCode
    {
        get; set;
    }
    public string Drawer
    {
        get; set;
    }
    public string BuyerName
    {
        get; set;
    }
    public string BuyerTaxId
    {
        get; set;
    }
    public string SellerName
    {
        get; set;
    }
    public string SellerTaxId
    {
        get; set;
    }
    public decimal? Amount
    {
        get; set;
    }
    public decimal? Tax
    {
        get; set;
    }
    public decimal? Total
    {
        get; set;
    }
    public string Remarks
    {
        get; set;
    }
    public RailwayTicketModel RailwayTicket
    {
        get; set;
    }
    public List<InvoiceItemModel> Items { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool RequiresReview => Warnings.Count > 0;
}

public sealed record RailwayTicketModel(string PassengerName, string IdNumber, string TrainNumber, string DepartureStation, string DestinationStation, DateOnly? TravelDate, string DepartureTime, string SeatLevel, string Carriage, string Seat, string ETicketNumber, string BusinessType);
public record StructuredFieldEvidence(string Path, string Value, string Source = "xml");
public record ParseResult(InvoiceModel Invoice, IReadOnlyList<TextBox> Evidence, List<string> Issues, IReadOnlyList<StructuredFieldEvidence> StructuredFields = null)
{
    public bool NeedsReview => Issues.Count > 0;
}

public record JobView(Guid Id, string State, DateTimeOffset CreatedAt, ParseResult Result = null, string Error = null);
public sealed class InvoiceRecognitionOptions
{
    public string TempDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "App_Data", "recognition");
    public int MaxPages { get; set; } = 30;
    public int MaxJobs { get; set; } = 500;
    public long MaxBytes { get; set; } = 20 * 1024 * 1024;
    public int QueueCapacity { get; set; } = 16;
    public int Workers { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 120;
    public string ApiKey
    {
        get; set;
    }
    public string PdfToPpm { get; set; } = "pdftoppm";
    public string DetModel
    {
        get; set;
    }
    public string ClsModel
    {
        get; set;
    }
    public string RecModel
    {
        get; set;
    }
    public string Dictionary
    {
        get; set;
    }
}
