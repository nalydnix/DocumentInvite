using System.Text;

namespace DocumentInvite.Api.Features.Demo;

/// <summary>Builds a small one-page PDF (standard Helvetica font, no embedding) for the demo files.</summary>
public static class SamplePdf
{
    public static byte[] Create(string title, params string[] lines)
    {
        var content = new StringBuilder("BT /F1 18 Tf 72 760 Td (" + Escape(title) + ") Tj ET\n");
        var y = 725;
        foreach (var line in lines)
        {
            content.Append($"BT /F1 11 Tf 72 {y} Td ({Escape(line)}) Tj ET\n");
            y -= 18;
        }

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content.ToString())} >>\nstream\n{content}endstream",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Escape(string text)
    {
        var ascii = new StringBuilder();
        foreach (var c in text)
        {
            ascii.Append(c is '(' or ')' or (char)92 ? "\\" + c : c is >= ' ' and <= '~' ? c.ToString() : "?");
        }

        return ascii.ToString();
    }
}
