using System.Globalization;
using System.Text;

namespace JTW.NetworkScanner.Core;

/// <summary>Tabular export to CSV or PDF. Any module with results builds a header + rows and calls this.</summary>
public static class Exporter
{
    public static void ExportCsv(string path, IList<string> headers, IEnumerable<IList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Esc)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Esc)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

        static string Esc(string s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    public static void ExportPdf(string path, string title, string subtitle, IList<string> headers, IList<IList<string>> rows)
    {
        var pdf = new SimplePdf();
        pdf.WriteTable(title, subtitle, headers, rows);
        pdf.Save(path);
    }

    /// <summary>Shows a Save dialog and exports in the chosen format. Returns the saved path or null.</summary>
    public static string ExportWithDialog(string defaultName, string title, string subtitle, IList<string> headers, IList<IList<string>> rows)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{defaultName}-{DateTime.Now:yyyyMMdd-HHmm}",
            Filter = "PDF report (*.pdf)|*.pdf|CSV spreadsheet (*.csv)|*.csv",
            AddExtension = true
        };
        if (dlg.ShowDialog() != true) return null;
        if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ExportCsv(dlg.FileName, headers, rows);
        else
            ExportPdf(dlg.FileName, title, subtitle, headers, rows);
        return dlg.FileName;
    }
}

/// <summary>
/// Dependency-free PDF writer: landscape A4 tables using the built-in Helvetica fonts.
/// </summary>
public class SimplePdf
{
    private const float PageW = 842f, PageH = 595f, Margin = 36f;
    private readonly List<string> _pages = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public void WriteTable(string title, string subtitle, IList<string> headers, IList<IList<string>> rows)
    {
        const float fontSize = 8f, rowH = 15f, headerH = 18f;
        float usable = PageW - 2 * Margin;

        // Column widths from content length (sampled), clamped
        var lens = headers.Select((h, i) =>
        {
            var max = Math.Max(h.Length, rows.Take(500).Select(r => i < r.Count ? (r[i] ?? "").Length : 0).DefaultIfEmpty(0).Max());
            return (float)Math.Clamp(max, 4, 60);
        }).ToArray();
        var total = lens.Sum();
        var widths = lens.Select(l => usable * l / total).ToArray();

        int rowIndex = 0, pageNo = 0;
        do
        {
            pageNo++;
            var sb = new StringBuilder();
            float y = PageH - Margin;

            // Brand bar
            Rect(sb, 0, PageH - 8, PageW, 8, "0 0.706 1");
            Text(sb, Margin, y - 14, "F2", 15, title, "0.06 0.09 0.14");
            Text(sb, PageW - Margin - 170, y - 12, "F1", 8, $"{AppInfo.Brand} Network Scanner {AppInfo.VersionText}", "0.4 0.45 0.55");
            y -= 30;
            if (!string.IsNullOrEmpty(subtitle))
            {
                Text(sb, Margin, y, "F1", 9, subtitle, "0.3 0.35 0.45");
                y -= 16;
            }

            // Header row
            Rect(sb, Margin, y - headerH + 4, usable, headerH, "0.11 0.16 0.25");
            float x = Margin;
            for (int c = 0; c < headers.Count; c++)
            {
                Text(sb, x + 4, y - 9, "F2", fontSize, Fit(headers[c], widths[c] - 8, fontSize), "1 1 1");
                x += widths[c];
            }
            y -= headerH;

            int onPage = 0;
            while (rowIndex < rows.Count && y - rowH > Margin + 16)
            {
                var r = rows[rowIndex];
                if (onPage % 2 == 1) Rect(sb, Margin, y - rowH + 4, usable, rowH, "0.94 0.96 0.98");
                x = Margin;
                for (int c = 0; c < headers.Count; c++)
                {
                    var cell = c < r.Count ? r[c] ?? "" : "";
                    Text(sb, x + 4, y - 7, "F1", fontSize, Fit(cell, widths[c] - 8, fontSize), "0.1 0.1 0.15");
                    x += widths[c];
                }
                y -= rowH;
                rowIndex++; onPage++;
            }

            Text(sb, Margin, Margin - 6, "F1", 7, $"Generated {DateTime.Now:yyyy-MM-dd HH:mm}  •  {rows.Count} rows", "0.5 0.5 0.55");
            Text(sb, PageW - Margin - 40, Margin - 6, "F1", 7, $"Page {pageNo}", "0.5 0.5 0.55");
            _pages.Add(sb.ToString());
        } while (rowIndex < rows.Count);
    }

    private static string Fit(string s, float width, float size)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ');
        int maxChars = (int)(width / (size * 0.5f));
        if (maxChars < 2) return "";
        return s.Length <= maxChars ? s : s.Substring(0, Math.Max(1, maxChars - 1)) + "...";
    }

    private static void Text(StringBuilder sb, float x, float y, string font, float size, string text, string rgb)
    {
        sb.Append(Inv, $"BT {rgb} rg /{font} {size:0.##} Tf {x:0.##} {y:0.##} Td (");
        foreach (var ch in text)
        {
            if (ch == '(' || ch == ')' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 32) sb.Append(' ');
            else if (ch < 127) sb.Append(ch);
            else if (ch == '•') sb.Append("\\225");
            else if (ch == '…') sb.Append("...");
            else if (ch == '—' || ch == '–') sb.Append('-');
            else if (ch < 256) sb.Append('\\').Append(Convert.ToString(ch, 8).PadLeft(3, '0'));
            else sb.Append('?');
        }
        sb.Append(") Tj ET\n");
    }

    private static void Rect(StringBuilder sb, float x, float y, float w, float h, string rgb) =>
        sb.Append(Inv, $"{rgb} rg {x:0.##} {y:0.##} {w:0.##} {h:0.##} re f\n");

    public void Save(string path)
    {
        var objects = new List<string>();
        // 1 catalog, 2 pages, 3 font regular, 4 font bold, then per page: page obj + content obj
        int pageCount = _pages.Count;
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{5 + i * 2} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (int i = 0; i < pageCount; i++)
        {
            int contentId = 6 + i * 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW} {PageH}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
            var content = _pages[i];
            objects.Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        using var fs = File.Create(path);
        var offsets = new List<long>();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); fs.Write(b, 0, b.Length); }
        W("%PDF-1.4\n%âãÏÓ\n");
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(fs.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = fs.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
