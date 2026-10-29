using System.Text;

namespace Mitzh.Invoice.Core;

public static class InvoiceFileFormat
{
    /// <summary>Detect supported content from a prefix (up to 512 bytes), including XML BOMs.</summary>
    public static string Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("%PDF-"u8))
        {
            return ".pdf";
        }

        if (header.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return ".png";
        }

        if (header.StartsWith(new byte[] { 255, 216, 255 }))
        {
            return ".jpg";
        }

        if (header.StartsWith(new byte[] { 80, 75, 3, 4 }))
        {
            return ".ofd";
        }

        string prefix;
        if (header.StartsWith(new byte[] { 255, 254, 0, 0 }))
        {
            prefix = Encoding.UTF32.GetString(header);
        }
        else if (header.StartsWith(new byte[] { 0, 0, 254, 255 }))
        {
            prefix = new UTF32Encoding(true, true).GetString(header);
        }
        else if (header.StartsWith(new byte[] { 255, 254 }) || header.StartsWith(new byte[] { 60, 0 }))
        {
            prefix = Encoding.Unicode.GetString(header);
        }
        else if (header.StartsWith(new byte[] { 254, 255 }) || header.StartsWith(new byte[] { 0, 60 }))
        {
            prefix = Encoding.BigEndianUnicode.GetString(header);
        }
        else
        {
            prefix = Encoding.UTF8.GetString(header);
        }

        prefix = prefix.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (prefix.StartsWith('<') || (header.Length >= 512 && prefix.Length == 0))
        {
            return ".xml";
        }

        throw new InvalidDataException("Unsupported file content. Expected XML, OFD, PDF, PNG or JPEG.");
    }
}
