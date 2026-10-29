using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Mitzh.Invoice.Core;
// Read from the ZIP directly: never extract user-controlled archive paths.
public static class OfdReader
{
    public static List<TextBox> Read(string path, int maxPages, CancellationToken ct)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 4000 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024)
        {
            throw new InvalidDataException("OFD archive exceeds expanded size/entry limit.");
        }

        XDocument Xml(string name)
        {
            ZipArchiveEntry entry = zip.GetEntry(name) ?? throw new InvalidDataException("Missing OFD part: " + name);
            if (entry.Length > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("XML part too large.");
            }

            using Stream input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            return XDocument.Load(reader);
        }

        XElement[] roots = Xml("OFD.xml").Descendants().Where(e => e.Name.LocalName == "DocRoot").ToArray();
        if (roots.Length != 1)
        {
            throw new InvalidDataException("Only single-document OFD packages are supported; split the package first.");
        }

        string docPath = Resolve("", roots[0].Value);
        XDocument doc = Xml(docPath);
        XElement[] pages = doc.Descendants().Where(e => e.Name.LocalName == "Page" && e.Attribute("BaseLoc") != null).ToArray();
        if (pages.Length == 0 || pages.Length > maxPages)
        {
            throw new InvalidDataException("Invalid OFD page count.");
        }

        var result = new List<TextBox>();
        for (int p = 0; p < pages.Length; p++)
        {
            ct.ThrowIfCancellationRequested();
            XDocument page = Xml(Resolve(docPath, pages[p].Attribute("BaseLoc").Value));
            foreach (XElement obj in page.Descendants().Where(e => e.Name.LocalName == "TextObject"))
            {
                double[] boundary = Numbers(obj.Attribute("Boundary")?.Value);
                if (boundary.Length != 4)
                {
                    continue;
                }

                // CTM and transformed parent blocks require a complete graphics transform stack.
                if (obj.AncestorsAndSelf().Any(e => e.Attribute("CTM") != null))
                {
                    throw new InvalidDataException("Transformed OFD text requires a renderer; refusing inaccurate coordinates.");
                }

                foreach (XElement t in obj.Descendants().Where(e => e.Name.LocalName == "TextCode"))
                {
                    double x = Number(t.Attribute("X")?.Value);
                    double y = Number(t.Attribute("Y")?.Value);
                    if (!string.IsNullOrWhiteSpace(t.Value))
                    {
                        result.Add(new(p + 1, boundary[0] + x, boundary[1] + y, boundary[2], boundary[3], t.Value, "ofd-text"));
                    }
                }
            }
        }

        return result;
    }

    private static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0;
    private static double[] Numbers(string s) => (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
    internal static string Resolve(string owner, string value)
    {
        var parts = new List<string>();
        string full = value.StartsWith('/') ? value[1..] : (owner.Contains('/') ? owner[..(owner.LastIndexOf('/') + 1)] : "") + value;
        foreach (string part in full.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new InvalidDataException("Invalid OFD reference.");
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                if (part.Contains('\\') || part.Contains(':'))
                {
                    throw new InvalidDataException("Invalid OFD reference.");
                }

                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }
}
