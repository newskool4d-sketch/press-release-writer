using System.IO.Compression;
using PressRelease;

/// <summary>Deterministic guideline checks: discouraged wording, easy Korean, date style, timeliness, rejection baseline and the official form.</summary>
public static class GuidelineChecks
{
    public static void Run(Action<bool, string> require, string directory)
    {
        static string Draft(string body, string title = "제목") => $"## 보도자료 초안\n\n{title}\n\n{body}\n\n## 검토 의견\n확인";

        var discouraged = Writing.Check(Draft("학생 75명이 참여한 뜻깊은 연수가 매우 성공적으로 끝났다. 학생들은 안전을 제공받을 수 있다."));
        require(discouraged.Contains("지양 표현 검토") && discouraged.Contains("‘뜻깊’") && discouraged.Contains("‘매우 성공적’") &&
            discouraged.Contains("‘제공받을 수 있다’"), "guideline/flags discouraged expressions from the briefing");
        require(!Writing.Check(Draft("학생 75명이 협의회에서 논의를 거쳐 결정했다.")).Contains("지양 표현"), "guideline/plain sentence has no discouraged flag");

        var foreign = Writing.Check(Draft("학생 75명이 멘토링 프로젝트에 참여해 데이터를 분석했다. TF가 매뉴얼을 배포했다."));
        require(foreign.Contains("쉬운 우리말 검토") && foreign.Contains("멘토링→상담·지도") && foreign.Contains("프로젝트→계획·사업") &&
            foreign.Contains("데이터→자료·정보") && foreign.Contains("TF→전담반·전담팀") && foreign.Contains("매뉴얼→안내서·지침서") &&
            !foreign.Contains("멘토→"), "guideline/flags flagged foreign words once each");
        require(!Writing.Check(Draft("학생 75명이 STFC 동아리에서 활동했다.")).Contains("TF→"), "guideline/TF inside a Latin word is not flagged");
        require(Writing.Check(Draft("학생 75명의 멘토가 참여했다.")).Contains("멘토→지도자"), "guideline/standalone 멘토 is flagged");

        var dated = Writing.Check(Draft("학생 75명이 2026. 4. 18. 참여했다. 신청은 2026-05-01까지다."));
        require(dated.Contains("공문서식 날짜") && dated.Contains("‘2026. 4. 18.’") && dated.Contains("‘2026-05-01’"), "guideline/flags official document date styles");
        require(!Writing.Check(Draft("학생 75명이 4월 18일 참여했다.")).Contains("공문서식 날짜"), "guideline/press date style passes");
        var held = Writing.Check("## 보도자료 초안\n\n작성 보류\n\n뜻깊은 멘토링을 2026. 4. 18. 진행함.\n\n## 검토 의견\n확인");
        require(!held.Contains("지양 표현") && !held.Contains("쉬운 우리말") && !held.Contains("공문서식"), "guideline/held draft skips wording checks");

        // Timeliness: upload by 15:00 the day before, publication on the event day or the next day, three days or more is a rejection risk.
        static WritingContext Dates(string? start, string? end, string? publication, string? upload, string status = "확인 필요") =>
            new("직속기관", status, false, EventStartDate: start, EventEndDate: end, PublicationDate: publication, UploadAt: upload);
        var sameDay = Timeliness.Evaluate(Dates("2026-11-06", "2026-11-06", "2026-11-06", "2026-11-05 14:30"));
        require(sameDay.Any(line => line.Contains("행사 당일 보도")) && sameDay.Any(line => line.Contains("충족")), "timeliness/same-day publication with on-time upload");
        var nextDay = Timeliness.Evaluate(Dates("2026. 11. 5.", "2026년 11월 6일", "2026-11-07", "2026-11-06 15:00"));
        require(nextDay.Any(line => line.Contains("행사 익일 보도")) && nextDay.Any(line => line.Contains("충족")), "timeliness/next-day publication accepts mixed date styles and 15:00 exactly");
        var late = Timeliness.Evaluate(Dates("2026-11-03", "2026-11-03", "2026-11-06", "2026-11-05 15:01"));
        require(late.Any(line => line.Contains("3일 경과") && line.Contains("반려 가능성")) && late.Any(line => line.Contains("15시를 넘음")),
            "timeliness/three days later and a late upload are both flagged");
        require(Timeliness.Evaluate(Dates("2026-11-04", null, "2026-11-06", null)).Any(line => line.Contains("2일째") && line.Contains("담당자 확인")),
            "timeliness/second day is left to the officer and a missing end date falls back to the start date");
        require(Timeliness.Evaluate(Dates("2026-11-10", "2026-11-10", "2026-11-06", "2026-11-06 09:00")).Any(line => line.Contains("당일 이후 탑재")),
            "timeliness/upload on the publication day exceeds the deadline");
        var weekend = Timeliness.Evaluate(Dates("2026-11-09", "2026-11-09", "2026-11-09", "2026-11-06 14:00"));
        require(weekend.Any(line => line.Contains("일요일") && line.Contains("담당자 확인")) && weekend.Any(line => line.Contains("충족")),
            "timeliness/weekend deadline day is reported for the officer");
        require(Timeliness.Evaluate(Dates("2026-11-06", "2026-11-06", "2026-11-07", "2026-11-06")).Any(line => line.Contains("탑재 시각 확인")),
            "timeliness/deadline day without a time asks for the time");
        require(Timeliness.Evaluate(Dates("11/6", null, "2026-11-06", "내일")).Count(line => line.Contains("형식 확인")) == 2,
            "timeliness/malformed inputs are reported, not guessed");
        require(Timeliness.Evaluate(Dates(null, null, null, null)) is [var only] && only.Contains("계산 생략"), "timeliness/no dates skips the calculation");
        require(Timeliness.Evaluate(Dates("2026-11-06", "2026-11-06", "2026-11-04", null, "행사 완료")).Any(line => line.Contains("‘행사 완료’") && line.Contains("시제 확인")),
            "timeliness/completed status before the event is a tense mismatch");
        require(!Timeliness.Evaluate(Dates("2026-11-06", "2026-11-06", "2026-11-07", null, "행사 완료")).Any(line => line.Contains("시제 확인")),
            "timeliness/completed status after the event is consistent");
        require(Timeliness.Evaluate(Dates("2026-11-06", "2026-11-08", "2026-11-07", null, "행사 예정")).Any(line => line.Contains("‘행사 예정’")),
            "timeliness/planned status during the event is a tense mismatch");
        require(Timeliness.ParseDate("2026-11-06") == new DateOnly(2026, 11, 6) && Timeliness.ParseDate("20261106") == new DateOnly(2026, 11, 6) &&
            Timeliness.ParseDate("2026-13-01") == null && Timeliness.PressDate(new DateOnly(2026, 11, 6)) == "11월 6일(금)" &&
            Timeliness.OfficialDate(new DateOnly(2026, 11, 6)) == "2026. 11. 6.(금)", "timeliness/date parsing and formatting");
        require(Timeliness.Parse("2026-11-05 오후 2시 30분", out var hasTime, out _) == new DateTime(2026, 11, 5, 14, 30, 0) && hasTime,
            "timeliness/Korean afternoon time is parsed");

        // Rejection baseline comes from the officer; the prompt carries it and the newly added categories.
        var rejections = Writing.RejectionReasons.Select(reason => new RejectionCheck(reason, Writing.NotApplicable)).ToList();
        rejections[5] = rejections[5] with { Status = Writing.Applicable };
        var context = new WritingContext("직속기관", "행사 완료", false, OfficialName: "예시교육기관", EventStartDate: "2026-11-06",
            EventEndDate: "2026-11-06", PublicationDate: "2026-11-06", UploadAt: "2026-11-05 14:30", Photo: "있음",
            Department: "교학과 교육과정운영팀", Rejections: rejections);
        var prompt = Writing.Prompt("원자료", context);
        require(prompt.Contains("협의 단계(협약 체결 전 협의회 등)=해당") && prompt.Contains("기보도 사안(동일 사안 재보도)=비해당") &&
            prompt.Contains("협약 체결 전 협의 단계") && prompt.Contains("단순 강사 소개"), "guideline/prompt carries the officer's rejection baseline and new categories");
        require(prompt.Contains("시의성 자동 점검(앱 계산): 행사 당일 보도") && prompt.Contains("다시 계산하지 않는다") &&
            prompt.Contains("사진 여부: 있음") && prompt.Contains("초상권 동의"), "guideline/prompt embeds the computed timeliness and photo guidance");
        require(Writing.Prompt("원자료", "직속기관", "확인 필요", false).Contains("반려 사유 사전 점검(담당자 입력): 미입력 · 전 항목 미확인"),
            "guideline/prompt without checks stays unverified");
        var conditions = Writing.ContextCheck(context);
        require(conditions.Contains("반려 사유 ‘해당’ 1건: 협의 단계") && conditions.Contains("작성 보류 검토") && conditions.Contains("사진 있음") &&
            !conditions.Contains("담당 부서·팀 미입력"), "guideline/context check reports applicable reasons and photo");
        var empty = Writing.ContextCheck(new WritingContext("확인 필요", "확인 필요", false));
        require(empty.Contains("계산 생략") && empty.Contains("전 항목 미확인") && empty.Contains("사진 여부 미입력") && empty.Contains("담당 부서·팀 미입력"),
            "guideline/empty conditions list what is missing");

        // Official form: header placeholders are filled, the red authoring guides are replaced by the draft paragraphs, the contact table stays.
        var draft = "## 보도자료 초안\n\n**예시교육기관, 학생 75명과 인공지능(AI) 체험 운영**\n\n예시교육기관(원장 ○○○)은 11월 6일 학생 75명을 대상으로 체험을 운영했다.\n\n이번 체험은 모둠 활동으로 진행했다.\n\n관계자는 \"확대할 계획\"이라고 말했다.\n\n## 검토 의견\n확인";
        var fields = Writing.FormFields(draft, context);
        require(fields.Title == "예시교육기관, 학생 75명과 인공지능(AI) 체험 운영" && fields.Paragraphs.Count == 3 &&
            fields.PublicationDate == new DateOnly(2026, 11, 6) && fields.Photo == "있음" && fields.Organization == "예시교육기관",
            "form/fields come from the draft title, paragraphs and conditions");
        var hwpx = Path.Combine(directory, "양식.hwpx");
        File.WriteAllBytes(hwpx, PressForm.Fill(fields));
        using (var zip = ZipFile.OpenRead(hwpx))
            require(zip.Entries[0].FullName == "mimetype" && zip.Entries[0].CompressedLength == zip.Entries[0].Length &&
                zip.Entries.Any(entry => entry.FullName == "BinData/image1.png"), "form/mimetype stays first and stored, images kept");
        var text = SourceFiles.Read(hwpx).Text;
        require(text.Contains("11월 6일(금)부터 보도하여") && text.Contains("(있음) -") && text.Contains("교학과 교육과정운영팀") &&
            text.Contains("(예시교육기관)") && text.Contains("제공일: 2026. 11. 6.(금)"), "form/header fields are filled");
        require(text.Contains(fields.Title) && text.Contains("예시교육기관(원장 ○○○)은 11월 6일") && text.Contains("\"확대할 계획\"") &&
            text.Contains("상단의 담당자 및 연락처로 문의") && !text.Contains("공공저작물 자유이용 허락 관련 표"), "form/body paragraphs and the closing notice are present, author reminder removed");
        require(!text.Contains("[제목 기재]") && !text.Contains("휴면명조") && !text.Contains("휴먼명조") && !text.Contains("(서식)") &&
            !text.Contains("사업목적") && !text.Contains("기대효과 또는 추후 계획") && !text.Contains("월  일(요일)"),
            "form/authoring guides are removed");
        var section = ReadEntry(hwpx, "Contents/section0.xml");
        require(!section.Contains("linesegarray") && section.Contains("&lt;") == false && section.Contains("&quot;확대할 계획&quot;") &&
            !section.Contains("paraPrIDRef=\"22\""), "form/line layout cache removed, text escaped, body uses the plain paragraph style");
        var minimal = PressForm.Fill(new PressFormFields("제목 <A&B>", ["본문 한 문단"]));
        File.WriteAllBytes(Path.Combine(directory, "최소.hwpx"), minimal);
        var minimalText = SourceFiles.Read(Path.Combine(directory, "최소.hwpx")).Text;
        require(minimalText.Contains("제목 <A&B>") && minimalText.Contains("본문 한 문단") && minimalText.Contains("월  일(요일)") &&
            minimalText.Contains("(있음 / 없음)") && minimalText.Contains("0000과 0000팀"), "form/unknown header values keep the placeholders for the officer");
        require(minimalText.Contains("제공일:     .   .   .(요일)") && !minimalText.Contains("2024. 10. 11."), "form/missing date leaves a blank 제공일 instead of the sample date");
        File.WriteAllBytes(Path.Combine(directory, "탭.hwpx"), PressForm.Fill(new PressFormFields("제목\t탭", ["앞\t뒤 글자"], Department: "교학과\t팀")));
        var tabSection = ReadEntry(Path.Combine(directory, "탭.hwpx"), "Contents/section0.xml");
        require(!tabSection.Contains('\t') && tabSection.Contains("제목 탭") && tabSection.Contains("앞 뒤 글자") && tabSection.Contains("교학과 팀"),
            "form/tabs become spaces so HWP keeps the text after them");
        var metadata = ReadEntry(hwpx, "Contents/content.hpf");
        require(metadata.Contains("name=\"creator\" content=\"text\">인천광역시교육청<") && metadata.Contains("name=\"lastsaveby\" content=\"text\">인천광역시교육청<") &&
            !metadata.Contains("서울특별시교육청"), "form/file properties carry the institution instead of personal names");
        require(Throws<InvalidDataException>(() => Writing.FormFields("## 보도자료 초안\n\n작성 보류\n\n사유", context)) &&
            Throws<InvalidDataException>(() => Writing.FormFields("리드문만 있음", context)) &&
            Throws<InvalidDataException>(() => PressForm.Fill(new PressFormFields(" ", ["본문"]))), "form/held or unstructured drafts are refused");
        var saved = Writing.Save(directory, draft, false, false, fields).Split('\n');
        require(saved is [var single] && single.EndsWith(".hwpx") && SourceFiles.Read(Path.Combine(directory, single)).Text.Contains(fields.Title),
            "form/save writes only the HWPX when asked");
        require(Throws<ArgumentException>(() => Writing.Save(directory, draft, false, false, null)), "form/save without any format is refused");
    }

    static string ReadEntry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
