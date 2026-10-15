using System.Globalization;
using System.Text;

namespace CashGear.Backup.UI.Components.Model;

/// <summary>What goes on a Recovery Kit. <see cref="Password"/> only when the user asked for it on the page.</summary>
public sealed record RecoveryKitInfo(string DestinationName, string Location, string MachineName, string MachineIdentity, DateTimeOffset Created)
{
    public string? Password { get; init; }
}

/// <summary>
/// The Recovery Kit: one printable A4 page with what is needed to get the backups back on this or another PC.
/// A plain PDF written by hand (standard fonts, no images, no dependencies). Locations and the password use a
/// fixed-width font so that l, 1, O and 0 can be told apart.
/// </summary>
public static class RecoveryKit
{
    private const double PageWidth = 595;
    private const double PageHeight = 842;
    private const double Margin = 56;
    private const int BodyChars = 86;
    private const int MonoChars = 72;

    /// <summary>"CashGear Recovery Kit - Backup disk.pdf", with characters Windows does not allow in names replaced.</summary>
    public static string FileName(string destinationName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return $"CashGear Recovery Kit - {new string([.. destinationName.Select(c => invalid.Contains(c) ? '_' : c)]).Trim()}.pdf";
    }

    /// <summary>The page uses WinAnsi fonts: printable ASCII and Latin-1 only.</summary>
    public static bool CanPrint(string text) => text.All(IsPrintable);

    public static byte[] Create(RecoveryKitInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var page = new Page();
        page.Text("CashGear Backup - Recovery Kit", Font.Bold, 20, gapAfter: 10);
        page.Paragraph("Keep this page somewhere safe and away from this computer: in a drawer, a safe, or with someone you trust. " +
            "It is what you need to get your files back if this PC is lost, stolen or broken.");
        page.Paragraph($"Made on {info.Created.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} on the PC \"{info.MachineName}\" " +
            $"(backup name {info.MachineIdentity}).", gapAfter: 14);

        page.Text("Where the backups are", Font.Bold, 13, gapAfter: 4);
        page.Paragraph($"Name: {info.DestinationName}");
        page.Mono(info.Location, gapAfter: 14);

        page.Text("Password", Font.Bold, 13, gapAfter: 4);
        if (info.Password is { } password && CanPrint(password))
        {
            page.Mono(password);
        }
        else
        {
            if (info.Password is not null)
            {
                page.Paragraph("The password has characters this page cannot be printed with.");
            }

            page.Paragraph("Write the password here by hand, exactly as you typed it:");
            page.Rule();
        }

        page.Paragraph("The backups are encrypted with this password. Without it nobody can restore them, not even CashGear: " +
            "there is no way to reset it.", gapAfter: 14);

        page.Text("To get your files back, on this or another PC", Font.Bold, 13, gapAfter: 4);
        page.Paragraph("1. Install CashGear Backup.");
        page.Paragraph("2. Connect the drive or network share that holds the backups.");
        page.Paragraph("3. Choose Add a backup, then \"Use a backup made earlier\", pick the folder above and enter the password.");
        page.Paragraph("4. Open Restore files for that backup and choose what to restore.");
        return page.ToPdf();
    }

    private static bool IsPrintable(char c) => c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ';

    private enum Font
    {
        Regular,
        Bold,
        Mono,
    }

    private sealed class Page
    {
        private readonly StringBuilder _content = new();
        private double _y = PageHeight - Margin;

        public void Text(string text, Font font, double size, double gapAfter = 2)
        {
            _y -= size;
            _content.Append(CultureInfo.InvariantCulture,
                $"BT /{FontName(font)} {size:0.#} Tf {Margin:0.#} {_y:0.#} Td ({Escape(text)}) Tj ET\n");
            _y -= gapAfter + (size * 0.35);
        }

        /// <summary>Body text, wrapped at word boundaries (very long words are cut).</summary>
        public void Paragraph(string text, double gapAfter = 2)
        {
            var lines = Wrap(text, BodyChars, atSpaces: true);
            for (var i = 0; i < lines.Count; i++)
            {
                Text(lines[i], Font.Regular, 11, i == lines.Count - 1 ? gapAfter : 1);
            }
        }

        /// <summary>Fixed-width text (paths, the password), cut into equal pieces when long.</summary>
        public void Mono(string text, double gapAfter = 2)
        {
            var lines = Wrap(text, MonoChars, atSpaces: false);
            for (var i = 0; i < lines.Count; i++)
            {
                Text(lines[i], Font.Mono, 11, i == lines.Count - 1 ? gapAfter : 1);
            }
        }

        /// <summary>A line to write on.</summary>
        public void Rule()
        {
            _y -= 26;
            _content.Append(CultureInfo.InvariantCulture, $"0.6 w {Margin:0.#} {_y:0.#} m {PageWidth - Margin:0.#} {_y:0.#} l S\n");
            _y -= 10;
        }

        public byte[] ToPdf()
        {
            var stream = Encoding.Latin1.GetBytes(_content.ToString());
            string[] objects =
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0} {PageHeight:0}] /Resources << /Font << /F1 4 0 R /F2 5 0 R /F3 6 0 R >> >> /Contents 7 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
            ];

            using var pdf = new MemoryStream();
            var offsets = new List<long>();
            Write(pdf, "%PDF-1.4\n%âãÏÓ\n");
            for (var i = 0; i < objects.Length; i++)
            {
                offsets.Add(pdf.Position);
                Write(pdf, string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n"));
            }

            offsets.Add(pdf.Position);
            Write(pdf, string.Create(CultureInfo.InvariantCulture, $"{objects.Length + 1} 0 obj\n<< /Length {stream.Length} >>\nstream\n"));
            pdf.Write(stream);
            Write(pdf, "\nendstream\nendobj\n");

            var xref = pdf.Position;
            var table = new StringBuilder();
            table.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                table.Append(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(pdf, table.ToString());
            return pdf.ToArray();
        }

        private static string FontName(Font font) => font switch
        {
            Font.Bold => "F2",
            Font.Mono => "F3",
            _ => "F1",
        };

        private static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                escaped.Append(c switch
                {
                    '\\' => "\\\\",
                    '(' => "\\(",
                    ')' => "\\)",
                    _ when IsPrintable(c) => c.ToString(),
                    _ => "?",
                });
            }

            return escaped.ToString();
        }

        private static List<string> Wrap(string text, int width, bool atSpaces)
        {
            var lines = new List<string>();
            var rest = text;
            while (rest.Length > width)
            {
                var cut = atSpaces ? rest.LastIndexOf(' ', width) : -1;
                if (cut <= 0)
                {
                    cut = width;
                }

                lines.Add(rest[..cut].TrimEnd());
                rest = rest[cut..].TrimStart(atSpaces ? ' ' : '\0');
            }

            lines.Add(rest);
            return lines;
        }

        private static void Write(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));
    }
}
