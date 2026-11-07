using PressRelease;

public static class WritingChecks
{
    public static void Run(Action<bool, string> require)
    {
        const string Lead = "학생 75명이 참여했다.";
        const string Body = "모둠별 활동을 진행했다.";
        var expected = Lead.Length + 1 + Body.Length;
        var separated = Draft($"{Lead}\n\n{Body}");
        var joinedTitle = $"## 보도자료 초안\n\n제목\n{Lead}\n\n{Body}\n\n## 검토 의견\n확인";
        require(Writing.Check(separated).Contains($"본문 추정 {expected}자"), "writing/count canonical paragraphs");
        require(Writing.Check(joinedTitle).Contains($"본문 추정 {expected}자"), "writing/count title without blank line");
        require(Writing.Check(joinedTitle.Replace("\n", "\r\n")).Contains($"본문 추정 {expected}자"), "writing/count CRLF title without blank line");
        require(Writing.Check(separated).Contains($"리드 추정 {Lead.Length}자"), "writing/lead count");

        var markdown = $"## 보도자료 초안\n\n### **제목**\n\n**{Lead}**\n\n### 본문\n{Body}\n\n## **검토 의견**\n여기는 분량에서 제외한다.";
        require(Writing.Check(markdown).Contains($"본문 추정 {expected}자"), "writing/count markdown headings and emphasis");
        var labels = $"## 보도자료 초안\n[제목]\n제목\n[리드문]\n{Lead}\n[본문]\n{Body}\n[검토 의견]\n확인";
        require(Writing.Check(labels).Contains($"본문 추정 {expected}자") &&
            Writing.Check(labels).Contains($"리드 추정 {Lead.Length}자"), "writing/count explicit section labels");
        require(Writing.Check($"# 제목\n\n{Lead}\n\n{Body}").Contains($"본문 추정 {expected}자"), "writing/count markdown title without draft header");
        require(Writing.Check(Writing.FinalizeDraft(separated)).Contains($"본문 추정 {expected}자"), "writing/count excludes review and footer");
        require(Writing.Check(Draft("학생 1명 😀")).Contains("본문 추정 7자"), "writing/count Unicode scalar values");

        var titleOnly = Writing.Check("## 보도자료 초안\n\n제목");
        require(titleOnly.Contains("본문이 없습니다") && !titleOnly.Contains("본문 추정"), "writing/title only has no body count");
        var ambiguous = Writing.Check($"{Lead}\n\n{Body}");
        require(ambiguous.Contains("분량 미계산"), "writing/ambiguous title does not discard content");
        require(Writing.Check($"## 보도자료 초안\n[리드문]\n{Lead}").Contains("분량 미계산"), "writing/missing title with explicit lead label");
        var held = Writing.Check("## 보도자료 초안\n\n작성 보류\n\n교직원 40명 대상 연수로 확인됨.\n\n## 검토 의견\n확인");
        require(held.Contains("분량 점검 제외") && !held.Contains("본문 추정") && !held.Contains("‘명’ 표기 확인"), "writing/held draft skips normal body checks");

        require(Writing.Check(Draft("AI 교육을 시작한다. 인공지능(AI)을 배운다.")).Contains("첫 사용"), "writing/AI later expansion does not fix first use");
        require(!Writing.Check(Draft("인공지능(AI) 교육을 시작한다. AI를 배운다.")).Contains("첫 사용"), "writing/AI correct first expansion");
        require(Writing.Check(Draft("AI교육에 참여했다.")).Contains("첫 사용"), "writing/AI Korean-adjacent first use");
        require(Writing.Check(Draft("인공지능(AI)을 배운다.", "AI 교육 안내")).Contains("첫 사용"), "writing/AI title participates in first-use check");
        require(!Writing.Check(Draft(Lead) + "\nAI 표기를 검토했다.").Contains("첫 사용"), "writing/AI in review does not affect draft check");
        require(!Writing.Check(Draft("AIDE라는 명칭을 사용했다.")).Contains("첫 사용"), "writing/AI substring in Latin word excluded");

        var context = new WritingContext("직속기관", "행사 완료", true,
            OfficialName: "예시교육기관", EventStartDate: "2026-11-01", EventEndDate: "2026-11-02",
            PublicationDate: "2026-11-03", UploadAt: "2026-11-02 14:00", TargetCount: "학생 75명",
            PriorCoverage: "기사 자료 미제공");
        var prompt = Writing.Prompt(Lead, context);
        require(prompt.Contains("공식 기관명: 예시교육기관") && prompt.Contains("행사 종료일: 2026-11-02") &&
            prompt.Contains("보도 희망일: 2026-11-03") && prompt.Contains("탑재 예정일·시각: 2026-11-02 14:00") &&
            prompt.Contains("대상과 대상 인원: 학생 75명"), "writing/prompt explicit context");
        require(prompt.Contains("시행일 원문 확인 필요") && !prompt.Contains("2026년 4월 27일") &&
            prompt.Contains("3일 이상"), "writing/prompt guideline date uncertainty and three-day criterion");
        require(prompt.Contains("문제 문장(개인정보 마스킹) → 문제 이유 → 수정 방향") &&
            prompt.Contains("별도 마무리를 생략") && prompt.Contains("공개 사용이 확인된"), "writing/prompt review privacy and missing closing");
        require(prompt.Contains("기존 초안에만 있는 수치·발언") && prompt.Contains("충돌 항목") &&
            prompt.Contains("50명 이하 기준을 교직원 연수가 아닌 다른 행사에 일반화하지 않는다"), "writing/prompt evidence and rejection scope");
        var minimal = Writing.Prompt(Lead, "직속기관", "확인 필요", false);
        require(minimal.Contains("행사 시작일: 확인 필요") && minimal.Contains("미제공 · 기보도 여부 미확인"), "writing/prompt missing context remains unknown");
        require(Writing.Prompt(Lead, context with { OfficialName = "기관\n이름" }).Contains("공식 기관명: 기관 이름"), "writing/prompt single-line context values");
        require(Throws<ArgumentException>(() => Writing.Prompt(" ", context)), "writing/empty source rejected");
        require(Throws<ArgumentException>(() => Writing.Prompt(new string('가', 50_001), context)), "writing/oversize source rejected");
    }

    static string Draft(string body, string title = "제목") =>
        $"## 보도자료 초안\n\n{title}\n\n{body}\n\n## 검토 의견\n확인";

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
