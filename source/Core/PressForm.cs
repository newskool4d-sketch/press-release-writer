using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace PressRelease;

/// <summary>Values that go into the official Incheon press-release form (header table and body).</summary>
public sealed record PressFormFields(
    string Title,
    IReadOnlyList<string> Paragraphs,
    DateOnly? PublicationDate = null,
    string? Photo = null,
    string? Department = null,
    string? Organization = null);

/// <summary>
/// Fills the official empty press-release form (인천광역시교육청 보도자료 양식) that is embedded in this library.
/// The header tables, logos and styles stay as they are; only the placeholder texts are replaced and the red
/// authoring guides between the title and the contact notice are swapped for the draft paragraphs.
/// The 자료문의 name and phone cells are empty in the form and are left for the officer to fill in HWP.
/// </summary>
public static class PressForm
{
    const string ResourceName = "PressRelease.PressForm.hwpx";
    const string SectionEntry = "Contents/section0.xml";
    const string TitlePlaceholder = "[제목 기재]";
    const string ContactTable = "<hp:tbl id=\"1241227521\"";
    const string ReminderText = "(위 공공저작물 자유이용 허락 관련 표를 꼭 넣어주세요)";
    static readonly Regex LineSegments = new("<hp:linesegarray>.*?</hp:linesegarray>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static byte[] Fill(PressFormFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (string.IsNullOrWhiteSpace(fields.Title)) throw new InvalidDataException("양식에 넣을 제목이 없습니다.");
        if (fields.Paragraphs.Count == 0 || fields.Paragraphs.All(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("양식에 넣을 본문이 없습니다.");

        using var template = typeof(PressForm).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("내장된 보도자료 양식을 찾을 수 없습니다.");
        using var source = new ZipArchive(template, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The mimetype entry must stay first and uncompressed for HWPX readers.
            foreach (var entry in source.Entries.OrderBy(entry => entry.FullName == "mimetype" ? 0 : 1))
            {
                var level = entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var copy = target.CreateEntry(entry.FullName, level);
                using var reader = entry.Open();
                using var writer = copy.Open();
                if (entry.FullName == SectionEntry)
                {
                    using var text = new StreamReader(reader, new UTF8Encoding(false));
                    var filled = FillSection(text.ReadToEnd(), fields);
                    var bytes = new UTF8Encoding(false).GetBytes(filled);
                    writer.Write(bytes, 0, bytes.Length);
                }
                else reader.CopyTo(writer);
            }
        }
        return output.ToArray();
    }

    internal static string FillSection(string xml, PressFormFields fields)
    {
        var text = xml;
        if (fields.PublicationDate is { } date)
        {
            text = ReplaceOnce(text, "<hp:t>  월  일(요일)</hp:t>", $"<hp:t> {Escape(Timeliness.PressDate(date))}</hp:t>");
            text = ReplaceOnce(text, "<hp:t>제공일: 2024. 10. 11.(금)</hp:t>", $"<hp:t>제공일: {Escape(Timeliness.OfficialDate(date))}</hp:t>");
        }
        // Without a date the form's sample date would read as a real one, so it becomes a blank like the other placeholders.
        else text = ReplaceOnce(text, "<hp:t>제공일: 2024. 10. 11.(금)</hp:t>", "<hp:t>제공일:     .   .   .(요일)</hp:t>");
        if (fields.Photo is "있음" or "없음")
            text = ReplaceOnce(text, "<hp:t> (있음 / 없음) - </hp:t>", $"<hp:t> ({fields.Photo}) - </hp:t>");
        if (!string.IsNullOrWhiteSpace(fields.Department))
            text = ReplaceOnce(text, "<hp:t>0000과 0000팀</hp:t>", $"<hp:t>{Escape(OneLine(fields.Department))}</hp:t>");
        if (!string.IsNullOrWhiteSpace(fields.Organization))
            text = ReplaceOnce(text, "<hp:t>(00교육지원청 00과)</hp:t>", $"<hp:t>({Escape(OneLine(fields.Organization).Trim('(', ')'))})</hp:t>");

        var titleIndex = text.IndexOf(TitlePlaceholder, StringComparison.Ordinal);
        if (titleIndex < 0) throw new InvalidDataException("양식의 제목 자리를 찾을 수 없습니다.");
        text = string.Concat(text.AsSpan(0, titleIndex), Escape(OneLine(fields.Title)), text.AsSpan(titleIndex + TitlePlaceholder.Length));

        // The authoring guides sit between the title table's paragraph and the paragraph that holds the contact table.
        var afterTitle = text.IndexOf("</hp:tbl>", titleIndex, StringComparison.Ordinal);
        var bodyStart = afterTitle < 0 ? -1 : text.IndexOf("</hp:p>", afterTitle, StringComparison.Ordinal);
        var contact = text.IndexOf(ContactTable, StringComparison.Ordinal);
        var bodyEnd = contact < 0 ? -1 : text.LastIndexOf("<hp:p ", contact, StringComparison.Ordinal);
        if (bodyStart < 0 || bodyEnd < 0 || bodyEnd < bodyStart) throw new InvalidDataException("양식의 본문 자리를 찾을 수 없습니다.");
        bodyStart += "</hp:p>".Length;

        var body = new StringBuilder();
        body.Append("<hp:p id=\"0\" paraPrIDRef=\"17\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\"><hp:run charPrIDRef=\"11\"/></hp:p>");
        foreach (var paragraph in fields.Paragraphs.Select(OneLine).Where(paragraph => paragraph.Length > 0))
        {
            // paraPr 20 is the form's plain justified body style; paraPr 22 carries the hanging indent of the red ▢ guides.
            body.Append("<hp:p id=\"0\" paraPrIDRef=\"20\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\">");
            body.Append("<hp:run charPrIDRef=\"34\"><hp:t>").Append(Escape(paragraph)).Append("</hp:t></hp:run></hp:p>");
            body.Append("<hp:p id=\"0\" paraPrIDRef=\"20\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\"><hp:run charPrIDRef=\"34\"/></hp:p>");
        }
        text = string.Concat(text.AsSpan(0, bodyStart), body.ToString(), text.AsSpan(bodyEnd));
        // The form already contains the 공공누리 table; the red reminder below it addresses the author, not the reader.
        var reminder = text.IndexOf(ReminderText, StringComparison.Ordinal);
        if (reminder >= 0)
        {
            var paragraphStart = text.LastIndexOf("<hp:p ", reminder, StringComparison.Ordinal);
            var paragraphEnd = text.IndexOf("</hp:p>", reminder, StringComparison.Ordinal);
            if (paragraphStart >= 0 && paragraphEnd > paragraphStart)
                text = string.Concat(text.AsSpan(0, paragraphStart), text.AsSpan(paragraphEnd + "</hp:p>".Length));
        }
        // Cached line layout belongs to the placeholder texts; HWP recomputes it when the array is absent.
        return LineSegments.Replace(text, "");
    }

    static string ReplaceOnce(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    // A raw tab inside <hp:t> makes HWP drop the text that follows it, so tabs become plain spaces.
    static string OneLine(string value) => Regex.Replace(value.Replace('\t', ' ').Trim(), @"\s*[\r\n]+\s*", " ");

    static string Escape(string value) => SecurityElement.Escape(value);
}
