using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PressRelease;

public record SourceText(string Text, string Note);

public static class SourceFiles
{
    public const int MaxInput = 50_000;
    const long MaxBytes = 25 * 1024 * 1024;

    public static SourceText Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0 || stream.Length > MaxBytes)
            throw new InvalidDataException("비어 있거나 25MB를 넘는 파일입니다. 필요한 내용을 붙여넣어 주세요.");

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" => ReadText(stream),
            ".hwpx" => ReadHwpx(stream),
            ".pdf" => ReadPdf(stream),
            _ => throw new InvalidDataException("TXT·MD·HWPX·PDF 파일을 선택해 주세요.")
        };
    }

    static SourceText ReadText(Stream stream)
    {
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var data = bytes.ToArray();
        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            return new(Encoding.Unicode.GetString(data, 2, data.Length - 2), "UTF-16 LE");
        if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            return new(Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2), "UTF-16 BE");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(data).TrimStart('\ufeff');
            if (text.Contains('\0')) throw new InvalidDataException("텍스트 파일의 인코딩을 확인해 주세요.");
            return new(text, "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                return new(Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(data), "CP949로 읽었습니다. 한글이 올바른지 확인해 주세요.");
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("텍스트 파일의 인코딩을 확인해 주세요. UTF-8로 저장한 뒤 다시 열어 주세요.");
            }
        }
    }

    static SourceText ReadHwpx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sections = zip.Entries.Select(e => (Entry: e, Match: Regex.Match(e.FullName, @"^Contents/section(\d{1,8})\.xml$", RegexOptions.IgnoreCase)))
            .Where(x => x.Match.Success).OrderBy(x => int.Parse(x.Match.Groups[1].Value)).Select(x => x.Entry).ToArray();
        if (sections.Length == 0 || sections.Length > 100 || sections.Sum(e => (decimal)e.Length) > MaxBytes)
            throw new InvalidDataException("HWPX의 본문 구역을 읽을 수 없거나 문서가 너무 큽니다.");

        var output = new StringBuilder();
        foreach (var section in sections)
        {
            using var part = section.Open();
            using var xml = XmlReader.Create(part, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes
            });
            var document = XDocument.Load(xml, LoadOptions.PreserveWhitespace);
            var paragraphs = document.Descendants().Where(e => e.Name.LocalName == "p" && !e.Ancestors().Any(a => a.Name.LocalName == "p"));
            foreach (var paragraph in paragraphs)
                output.AppendLine(ReadParagraph(paragraph).Trim());
        }
        var text = output.ToString().Trim();
        if (text.Length == 0) throw new InvalidDataException("HWPX에서 읽을 수 있는 본문이 없습니다.");
        return new(text, "HWPX 문단·표·글상자·각주를 읽었습니다. 그림이나 복잡한 서식의 내용은 원본에서 확인해 주세요.");
    }

    static string ReadParagraph(XElement paragraph)
    {
        var output = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            var parent = element.Ancestors().FirstOrDefault(e => e.Name.LocalName == "p");
            if (parent != paragraph) continue;
            switch (element.Name.LocalName)
            {
                case "t":
                    foreach (var node in element.Nodes())
                        output.Append(node is XText text ? text.Value :
                            node is XElement space && space.Name.LocalName is "lineBreak" ? "\n" : " ");
                    break;
                case "p":
                    // Text boxes, footnotes and captions hold their own paragraphs. Table cells are read with their table,
                    // and hidden comments and memos are notes to the author, not source text to send.
                    if (!element.Ancestors().TakeWhile(ancestor => ancestor != paragraph)
                            .Any(ancestor => ancestor.Name.LocalName is "tc" or "hiddenComment" or "fieldBegin"))
                        output.AppendLine().AppendLine(ReadParagraph(element).Trim());
                    break;
                case "tbl":
                    output.AppendLine();
                    var rows = element.Descendants().Where(e => e.Name.LocalName == "tr" &&
                        e.Ancestors().First(a => a.Name.LocalName == "tbl") == element);
                    foreach (var row in rows)
                    {
                        var cells = row.Elements().Where(e => e.Name.LocalName == "tc");
                        output.AppendLine(string.Join(" | ", cells.Select(cell =>
                            string.Join(" / ", cell.Descendants().Where(e => e.Name.LocalName == "p" &&
                                e.Ancestors().First(a => a.Name.LocalName == "tc") == cell &&
                                e.Ancestors().FirstOrDefault(a => a.Name.LocalName == "p") == paragraph)
                                .Select(p => ReadParagraph(p).Trim())))));
                    }
                    break;
            }
        }
        return output.ToString();
    }

    static SourceText ReadPdf(Stream stream)
    {
        using var doc = PdfDocument.Open(stream);
        if (doc.NumberOfPages > 100)
            throw new InvalidDataException("PDF가 100쪽을 넘습니다. 필요한 쪽을 별도로 저장해 주세요.");
        var output = new StringBuilder();
        var missing = new List<int>();
        foreach (var page in doc.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (string.IsNullOrWhiteSpace(text)) missing.Add(page.Number);
            else output.AppendLine($"[PDF {page.Number}쪽]").AppendLine(text).AppendLine();
        }
        if (output.Length == 0)
            throw new InvalidDataException("텍스트가 없는 PDF입니다. 스캔한 자료는 문자 인식 후 붙여넣어 주세요.");
        return new(output.ToString().Trim(), missing.Count == 0
            ? "PDF 텍스트를 읽었습니다. 표와 다단의 순서를 확인해 주세요."
            : $"텍스트를 읽지 못한 쪽 {string.Join(", ", missing)}. 해당 쪽의 내용을 직접 보완해 주세요.");
    }
}
