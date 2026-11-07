using System.Globalization;
using System.Text.RegularExpressions;

namespace PressRelease;

/// <summary>
/// Deterministic timeliness checks from the guideline: upload by 15:00 the day before the requested
/// publication date, publication on the event day or the next day, rejection when three or more days have passed.
/// Holidays and the second day after an event are not settled by the guideline, so they are left to the officer.
/// </summary>
public static class Timeliness
{
    public static readonly TimeOnly UploadDeadline = new(15, 0);
    static readonly string[] Weekdays = ["일", "월", "화", "수", "목", "금", "토"];
    static readonly Regex DatePattern = new(
        @"^\s*(?<y>\d{4})\s*[-./년]\s*(?<m>\d{1,2})\s*[-./월]\s*(?<d>\d{1,2})\s*[.일]?\s*(?:\([^)]*\))?" +
        @"(?:\s*(?<ampm>오전|오후)?\s*(?<h>\d{1,2})\s*(?::\s*(?<min>\d{1,2})|시\s*(?:(?<min2>\d{1,2})\s*분?)?))?\s*$",
        RegexOptions.CultureInvariant);
    static readonly Regex CompactDate = new(@"^\s*(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})\s*$", RegexOptions.CultureInvariant);

    public static DateOnly? ParseDate(string? value) => Parse(value, out _, out _) is { } parsed ? DateOnly.FromDateTime(parsed) : null;

    /// <summary>Returns the date and, when the text carries one, the time. Null when the text is empty or not a date.</summary>
    public static DateTime? Parse(string? value, out bool hasTime, out bool malformed)
    {
        hasTime = false;
        malformed = false;
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "확인 필요") return null;
        var compact = CompactDate.Match(value);
        var match = compact.Success ? compact : DatePattern.Match(value);
        if (!match.Success)
        {
            malformed = true;
            return null;
        }
        if (!int.TryParse(match.Groups["y"].Value, out var year) || !int.TryParse(match.Groups["m"].Value, out var month) ||
            !int.TryParse(match.Groups["d"].Value, out var day) || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            malformed = true;
            return null;
        }
        var result = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        if (!compact.Success && match.Groups["h"].Success)
        {
            var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minuteText = match.Groups["min"].Success ? match.Groups["min"].Value : match.Groups["min2"].Value;
            var minute = minuteText.Length == 0 ? 0 : int.Parse(minuteText, CultureInfo.InvariantCulture);
            if (match.Groups["ampm"].Value == "오후" && hour < 12) hour += 12;
            if (match.Groups["ampm"].Value == "오전" && hour == 12) hour = 0;
            if (hour > 23 || minute > 59)
            {
                malformed = true;
                return null;
            }
            result = result.AddHours(hour).AddMinutes(minute);
            hasTime = true;
        }
        return result;
    }

    public static string Weekday(DateOnly date) => Weekdays[(int)date.DayOfWeek];

    /// <summary>Press-release body style, e.g. 4월 18일(금).</summary>
    public static string PressDate(DateOnly date) => $"{date.Month}월 {date.Day}일({Weekday(date)})";

    /// <summary>Official form header style, e.g. 2026. 4. 18.(금).</summary>
    public static string OfficialDate(DateOnly date) => $"{date.Year}. {date.Month}. {date.Day}.({Weekday(date)})";

    public static IReadOnlyList<string> Evaluate(WritingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lines = new List<string>();
        var start = Read(context.EventStartDate, "행사 시작일", lines);
        var end = Read(context.EventEndDate, "행사 종료일", lines);
        var publication = Read(context.PublicationDate, "보도 희망일", lines);
        var upload = Parse(context.UploadAt, out var uploadHasTime, out var uploadMalformed);
        if (uploadMalformed) lines.Add("탑재 예정 시각 형식 확인 (예: 2026-11-05 14:30)");
        start ??= end;
        end ??= start;
        if (start != null && end != null && end < start) lines.Add("행사 종료일이 시작일보다 앞섭니다 · 날짜 확인");

        if (publication != null && end != null && start != null && end >= start)
        {
            var publicationDate = DateOnly.FromDateTime(publication.Value);
            var startDate = DateOnly.FromDateTime(start.Value);
            var endDate = DateOnly.FromDateTime(end.Value);
            var elapsed = publicationDate.DayNumber - endDate.DayNumber;
            if (publicationDate < startDate) lines.Add($"행사 {startDate.DayNumber - publicationDate.DayNumber}일 전 보도 · 사전 보도(행사 예정 시제) 확인");
            else if (publicationDate <= endDate) lines.Add("행사 당일 보도 · 시의성 적합");
            else if (elapsed == 1) lines.Add("행사 익일 보도 · 시의성 적합");
            else if (elapsed == 2) lines.Add("행사 후 2일째 보도 · 안내 기준으로 확정할 수 없음, 담당자 확인");
            else lines.Add($"행사 종료 후 {elapsed}일 경과 · 3일 이상 경과로 반려 가능성");

            var status = context.EventStatus?.Trim();
            var mismatch = status switch
            {
                "행사 예정" => publicationDate >= startDate,
                "행사 진행 중" => publicationDate < startDate || publicationDate > endDate,
                "행사 완료" => publicationDate < endDate,
                _ => false
            };
            if (mismatch) lines.Add($"보도 시점 ‘{status}’과 행사일·보도 희망일이 맞지 않음 · 시제 확인");
        }

        if (publication != null && upload != null)
        {
            var publicationDate = DateOnly.FromDateTime(publication.Value);
            var deadlineDay = publicationDate.AddDays(-1);
            var deadline = deadlineDay.ToDateTime(UploadDeadline);
            var uploadDate = DateOnly.FromDateTime(upload.Value);
            if (uploadDate >= publicationDate) lines.Add("보도 희망일 당일 이후 탑재 · 전날 15시 기한 초과, 보도 희망일 조정 필요");
            else if (uploadDate < deadlineDay) lines.Add($"탑재 기한(보도 희망일 전날 {PressDate(deadlineDay)} 15시) 충족");
            else if (!uploadHasTime) lines.Add("보도 희망일 전날 탑재 · 15시 이전인지 탑재 시각 확인");
            else if (upload.Value <= deadline) lines.Add("탑재 기한(보도 희망일 전날 15시) 충족");
            else lines.Add("탑재 예정이 보도 희망일 전날 15시를 넘음 · 탑재를 앞당기거나 보도 희망일 조정");
            if (deadlineDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                lines.Add($"보도 희망일 전날이 {Weekday(deadlineDay)}요일 · 휴일 취급은 담당자 확인");
        }
        else if (publication != null && upload == null && !uploadMalformed)
            lines.Add($"탑재 기한: {PressDate(DateOnly.FromDateTime(publication.Value).AddDays(-1))} 15시까지 · 탑재 예정 시각 미입력");

        if (lines.Count == 0) lines.Add("행사일·보도 희망일 미입력 · 시의성 계산 생략");
        return lines;
    }

    static DateTime? Read(string? value, string label, List<string> lines)
    {
        var parsed = Parse(value, out _, out var malformed);
        if (malformed) lines.Add($"{label} 형식 확인 (예: 2026-11-06)");
        return parsed;
    }
}
