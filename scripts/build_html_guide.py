"""Build the offline v0.1.18 guide from public-safe, synthetic screenshots."""
from pathlib import Path
import base64
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parent.parent
SHOTS = ROOT / "docs/images"

def data_uri(path: Path, mime: str) -> str:
    return f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode("ascii")

HTML = r'''<!doctype html>
<html lang="ko">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="description" content="인천교육 보도자료 작성기 v0.1.18 사용 안내. AI 연결, 원자료 입력, 초안 생성, 맞춤법 검사와 TXT·MD 저장 방법.">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'sha256-@@SCRIPTHASH@@'; base-uri 'none'; form-action 'none'">
  <title>인천교육 보도자료 작성기 · v0.1.18 사용 안내</title>
  <style>
    :root { color-scheme: light; --paper:#fbfaf7; --ink:#253735; --muted:#526560; --line:#d6dfd9; --accent:#226c65; --wash:#edf4ef; }
    * { box-sizing:border-box; }
    [hidden] { display:none !important; }
    html { scroll-padding-top:2rem; font-size:16px; }
    html[data-theme="dark"] { color-scheme:dark; --paper:#182522; --ink:#ecf2ee; --muted:#b8c9c0; --line:#435b50; --accent:#8bd6c0; --wash:#253b32; }
    body { margin:0; background:var(--paper); color:var(--ink); font-family:"Segoe UI","Malgun Gothic",sans-serif; font-size:1rem; line-height:1.85; word-break:keep-all; overflow-wrap:anywhere; }
    .reading-tools { display:flex; flex-wrap:wrap; align-items:center; gap:.65rem 1rem; margin:1rem 0 1.5rem; padding:.85rem 0; border-block:1px solid var(--line); }
    .reading-tools label { display:flex; align-items:center; flex-wrap:wrap; gap:.5rem; }
    .reading-tools select,.reading-tools button { font:inherit; color:var(--ink); background:var(--wash); border:1px solid var(--line); border-radius:4px; padding:.3rem .5rem; }
    .reading-tools input { width:7rem; accent-color:var(--accent); }
    .reading-tools output { min-width:3rem; }
    .reading-tools :focus-visible { outline:3px solid var(--accent); outline-offset:3px; }
    .reading-tools button { cursor:pointer; }
    .reading-tools button:hover { border-color:var(--accent); }
    .reading-status { flex-basis:100%; font-size:.8rem; color:var(--muted); margin:0; }
    .mast img { background:white; padding:4px; border-radius:3px; }
    html[data-theme="dark"] .example { background:var(--wash); }
    html[data-theme="dark"] code,html[data-theme="dark"] kbd { background:var(--wash); }
    a { color:var(--accent); text-underline-offset:4px; }
    a:hover { text-decoration-thickness:2px; }
    a:focus-visible, summary:focus-visible { outline:3px solid var(--accent); outline-offset:5px; }
    .skip { position:absolute; left:1rem; top:-8rem; padding:.65rem 1rem; background:white; z-index:5; }
    .skip:focus { top:1rem; }
    .mast { max-width:1200px; margin:auto; padding:25px 32px; display:flex; gap:1.5rem; align-items:center; justify-content:space-between; border-bottom:1px solid var(--line); }
    .mast img { display:block; width:205px; height:auto; }
    .mast p { margin:0; color:var(--muted); font-size:.9rem; }
    .layout { max-width:1200px; margin:auto; display:grid; grid-template-columns:190px minmax(0,1fr); gap:52px; padding:42px 32px 64px; }
    nav { position:sticky; top:28px; align-self:start; font-size:.92rem; }
    nav p { margin:0 0 .6rem; color:var(--muted); font-size:.8rem; font-weight:650; letter-spacing:.05em; }
    nav ol { list-style:none; margin:0; padding:0; }
    nav li { border-bottom:1px solid var(--line); }
    nav a { display:block; padding:.55rem 0; text-decoration:none; }
    .version { margin:0 0 .55rem; font-size:.82rem; color:var(--accent); font-weight:650; }
    h1 { margin:0 0 1rem; font-size:clamp(1.8rem,3.8vw,2.65rem); letter-spacing:-.055em; line-height:1.3; font-weight:700; }
    .intro { max-width:45em; margin:0 0 1rem; color:var(--muted); font-size:1.06rem; }
    .meta { margin:0; color:var(--muted); font-size:.83rem; }
    section { padding-top:2.5rem; margin-top:2.5rem; border-top:1px solid var(--line); }
    h2 { margin:0 0 1.25rem; font-size:1.5rem; line-height:1.45; letter-spacing:-.035em; }
    h2 small { display:inline-block; margin-right:.75rem; color:var(--accent); font-size:.85rem; vertical-align:middle; }
    h3 { margin:1.6rem 0 .5rem; font-size:1.08rem; }
    p { margin:.5rem 0 .9rem; }
    ol,ul { padding-left:1.4rem; margin:.6rem 0 1rem; }
    li { margin:.4rem 0; padding-left:.15rem; }
    .quick { counter-reset:quick; list-style:none; padding:0; }
    .quick li { counter-increment:quick; position:relative; padding:.6rem 0 .8rem 2.65rem; margin:0; border-bottom:1px solid var(--line); }
    .quick li::before { content:counter(quick); position:absolute; left:0; top:.66rem; width:1.65rem; height:1.65rem; border:1px solid var(--accent); border-radius:50%; text-align:center; font-size:.85rem; line-height:1.6rem; color:var(--accent); }
    .quick strong { display:block; }
    .note { padding:.85rem 1.05rem; margin:1.25rem 0; border-left:3px solid var(--accent); background:var(--wash); }
    .note p { margin:0; }
    figure { margin:1.5rem 0; }
    figure img { display:block; width:100%; height:auto; border:1px solid var(--line); border-radius:6px; }
    figcaption { margin-top:.6rem; color:var(--muted); font-size:.83rem; line-height:1.65; }
    .table-wrap { overflow-x:auto; margin:1rem 0; }
    table { width:100%; border-collapse:collapse; text-align:left; font-size:.94rem; }
    caption { text-align:left; font-size:.85rem; color:var(--muted); margin-bottom:.6rem; }
    th,td { padding:.85rem .9rem; border-bottom:1px solid var(--line); vertical-align:top; }
    th { background:var(--wash); font-weight:650; }
    tbody th { width:27%; }
    code,kbd { font-family:inherit; font-size:.92em; background:#e9eee9; border:1px solid var(--line); padding:.08em .3em; border-radius:3px; }
    .example { padding:1rem 1.25rem; border:1px solid var(--line); border-radius:6px; background:white; }
    .example p:last-child { margin-bottom:0; }
    .example .label { color:var(--muted); font-size:.83rem; margin:0 0 .5rem; }
    details { border-bottom:1px solid var(--line); padding:.9rem 0; }
    summary { cursor:pointer; font-weight:650; }
    details p { margin:.8rem 0 .3rem; color:var(--muted); }
    footer { margin-top:3rem; padding-top:1.25rem; border-top:1px solid var(--line); font-size:.85rem; color:var(--muted); }
    @media(max-width:860px) {
      .mast { padding:20px 24px; }
      .layout { grid-template-columns:1fr; gap:30px; padding:28px 24px 48px; }
      nav { position:static; }
      nav ol { display:flex; flex-wrap:wrap; gap:.25rem 1rem; }
      nav li { border:0; }
      nav a { padding:.2rem 0; text-decoration:underline; }
      section { margin-top:2rem; padding-top:2rem; }
    }
    @media(max-width:480px) {
      .mast { align-items:flex-start; gap:.7rem; padding:18px; }
      .mast img { width:155px; }
      .mast p { font-size:.75rem; }
      .layout { padding:24px 18px 40px; }
      th,td { padding:.65rem .5rem; }
      tbody th { width:30%; }
    }
    @media print {
      :root,html[data-theme="dark"] { color-scheme:light; --paper:white; --ink:#111; --muted:#333; --line:#bbb; --accent:#225b50; --wash:#f3f5f3; }
      html { font-size:16px !important; }
      .reading-tools,noscript { display:none; }
      body { background:white; color:#111; font-size:10pt; line-height:1.7; }
      nav,.skip { display:none; }
      .mast { padding:0 0 12pt; }
      .layout { display:block; max-width:none; padding:18pt 0 0; }
      h1 { font-size:24pt; }
      h2 { break-after:avoid; font-size:16pt; }
      h3,summary { break-after:avoid; }
      section { padding-top:18pt; margin-top:18pt; }
      figure,tr,.note,.example { break-inside:avoid; }
      details { break-inside:avoid; }
      details::details-content { content-visibility:visible; display:block; }
      details > *:not(summary) { display:block; }
      a { color:inherit; }
      .table-wrap { overflow:visible; }
    }
  </style>
</head>
<body>
<a class="skip" href="#main">본문으로 이동</a>
<header class="mast">
  <img src="@@CI@@" alt="인천광역시교육청" width="1271" height="335">
  <p>보도자료 작성기<br>사용 안내 · v0.1.18</p>
</header>
<div class="layout">
<nav aria-label="사용 안내 목차">
  <p>사용 안내</p>
  <ol>
    <li><a href="#start">01 처음 시작하기</a></li>
    <li><a href="#connect">02 AI 연결하기</a></li>
    <li><a href="#source">03 원자료 입력하기</a></li>
    <li><a href="#draft">04 초안과 맞춤법</a></li>
    <li><a href="#save">05 TXT·MD 저장</a></li>
    <li><a href="#display">06 화면 설정</a></li>
    <li><a href="#update">07 자동 업데이트</a></li>
    <li><a href="#help">08 문제 해결</a></li>
  </ol>
</nav>
<main id="main">
<div class="version">인천교육 보도자료 작성기 · v0.1.18</div>
<div class="reading-tools" role="group" aria-label="안내서 보기 설정" hidden>
  <label for="guide-theme">화면 <select id="guide-theme"><option value="light">라이트 모드</option><option value="dark">다크 모드</option></select></label>
  <label for="guide-size">글자 크기 <input id="guide-size" type="range" min="14" max="22" step="1" value="16"><output id="guide-size-value" for="guide-size" aria-live="polite">16px</output></label>
  <button id="guide-reset" type="button">기본값으로</button>
  <p class="reading-status" id="guide-status">이 안내서의 화면만 바뀝니다. 설정은 이 브라우저에 저장됩니다.</p>
</div>
<noscript><p>화면 모드와 글자 조절은 브라우저의 JavaScript 허용이 필요합니다. 본문은 그대로 읽을 수 있습니다.</p></noscript>
<h1>자료를 넣고,<br>검토한 초안을 저장하세요.</h1>
<p class="intro">처음 실행부터 AI 연결, 초안 작성, 맞춤법 검사와 저장까지 순서대로 안내합니다. 화면의 버튼 이름을 기준으로 따라 하세요.</p>
<p><strong>앱에서 열기:</strong> 상단 <strong>도움말 → 사용 안내</strong>를 선택하면 이 안내서가 기본 브라우저에서 열립니다. 인터넷 연결 없이 읽을 수 있습니다.</p>
<p class="meta">Windows x64용 · 안내 기준 2026년 11월 7일 · 그림이 포함된 단일 HTML 파일</p>
<figure>
  <img src="@@WORKSPACE@@" alt="보도자료 작성기 화면. 상단 AI 선택과 설정, 왼쪽 원자료, 가운데 초안 작성·편집, 오른쪽 검토, 하단 저장 도구가 배치되어 있다." width="1264" height="791">
  <figcaption>화면 예시 — 입력 내용과 초안은 화면 검증용 가상 자료입니다. 실제 원고와 연결 상태는 사용자 환경에 따라 달라집니다.</figcaption>
</figure>

<section id="start">
  <h2><small>01</small>처음 시작하기</h2>
  <p><a href="https://github.com/newskool4d-sketch/press-release-writer/releases/tag/v0.1.18">v0.1.18 릴리스</a>에서 <code>PressReleaseWriter-win-x64.zip</code>을 내려받아 압축을 풀고 <code>PressReleaseWriter.exe</code>를 실행합니다. ZIP에는 .NET 런타임이 포함되어 있습니다. 로컬에 설치했다면 설치된 보도자료 작성기를 실행하세요.</p>
  <p>현재 배포본은 서명되지 않은 Windows x64용입니다. 실행 전 배포 저장소와 파일 출처를 확인하세요.</p>
  <ol class="quick">
    <li><strong>AI 연결</strong><a href="#connect">설정 → AI 연결</a>에서 로그인 또는 API 키 연결을 마칩니다.</li>
    <li><strong>원자료 입력</strong><a href="#source">원문</a>에는 근거 자료를, <a href="#source">작성 조건</a>에는 선택값과 확인 정보를 넣습니다.</li>
    <li><strong>초안 검토</strong><a href="#draft">초안 생성</a> 후 사실을 확인하고 맞춤법 수정안을 검토합니다.</li>
    <li><strong>파일 저장</strong><a href="#save">작업 폴더</a>를 고르고 TXT·MD·HWPX를 선택한 뒤 초안 저장을 누릅니다.</li>
  </ol>
</section>

<section id="connect">
  <h2><small>02</small>AI 연결하기</h2>
  <p>이 앱은 사용자가 선택한 AI 서비스에 연결해 원고를 작성합니다. 상단 톱니바퀴 <strong>설정 → AI 연결</strong>을 열고 아래에서 사용할 방식을 고르세요. CLI 방식은 해당 공식 CLI가 PC에 설치되어 있어야 합니다. <strong>설치 안내</strong>가 표시되면 안내된 공식 문서를 확인하세요.</p>
  <div class="table-wrap">
    <table>
      <caption>AI별 연결 방법</caption>
      <thead><tr><th scope="col">서비스</th><th scope="col">연결 순서와 확인 사항</th></tr></thead>
      <tbody>
        <tr><th scope="row">ChatGPT · Codex</th><td><strong>로그인</strong>을 누르고 열린 브라우저에서 ChatGPT 계정으로 인증합니다. 앱 전용 프로필을 사용하므로 다른 Codex 프로그램에 로그인했어도 앱에서는 처음 한 번 로그인할 수 있습니다. 브라우저 방식이 어려우면 <strong>장치 코드 로그인</strong>을 이용하세요.</td></tr>
        <tr><th scope="row">Claude Code</th><td>PC에 설치된 공식 Claude Code CLI의 기존 로그인을 사용합니다. 로그인되어 있지 않으면 <strong>로그인</strong>을 누르고 인증을 마칩니다.</td></tr>
        <tr><th scope="row">Gemini API</th><td><a href="https://aistudio.google.com/apikey">Google AI Studio</a>에서 발급한 API 키를 앱의 키 입력칸에 넣고 <strong>연결 확인</strong>을 누릅니다. 키는 앱을 닫을 때까지 사용합니다. 다시 실행하면 키를 입력해야 합니다. API 사용량·과금 조건은 사용하는 Google 프로젝트에서 확인하세요.</td></tr>
        <tr><th scope="row">기관용 Gemini CLI</th><td><strong>기관용 Gemini CLI 설정</strong>을 펼쳐 학교·기관용 Gemini Code Assist의 Google Cloud 프로젝트 ID를 넣고 <strong>계정 관리</strong>를 누릅니다. CLI에서 기관 계정으로 로그인한 뒤 <code>/quit</code>로 창을 닫습니다. 실제 연결은 초안 생성 또는 맞춤법 검사에서 확인합니다.</td></tr>
      </tbody>
    </table>
  </div>
  <p>인증을 마친 뒤 <strong>상태 새로고침</strong>으로 표시를 확인하고, 메인 화면 상단에서 사용할 AI를 선택합니다. 로그인 상태와 실제 사용 권한·사용량 한도는 다를 수 있습니다. 생성 과정에 표시되는 안내도 확인하세요.</p>
  <aside class="note" aria-label="AI 전송 안내">
    <p><strong>외부 AI 전송 동의</strong>를 켜야 생성과 검사를 실행할 수 있습니다. 동의는 앱을 실행할 때마다 꺼져 있습니다. 초안 생성 시 원자료·작성 조건·확인 정보가, 맞춤법 검사 시 현재 초안이 선택한 AI 서비스로 전송됩니다. 개인정보·비공개 내용을 먼저 제거하고 기관에서 승인한 서비스를 사용하세요.</p>
  </aside>
  <p>비밀번호와 API 키는 해당 서비스의 로그인 화면 또는 앱의 전용 입력칸에만 입력하세요. 원자료나 초안에는 넣지 마세요.</p>
</section>

<section id="source">
  <h2><small>03</small>원문과 작성 조건 구분하기</h2>
  <p>왼쪽 <strong>원자료</strong>의 두 탭을 나누어 사용합니다. 원문은 사실 확인의 근거로 다룹니다. <strong>작성 조건</strong>에서는 기관 구분·보도 시점·작성 유형을 선택하고 확인된 행사 정보를 입력합니다. 원문 안에 적힌 요청이나 명령을 별도 작성 지시로 취급하지 않습니다.</p>
  <div class="table-wrap">
    <table>
      <caption>입력칸별 넣을 내용</caption>
      <thead><tr><th scope="col">입력 위치</th><th scope="col">내용</th></tr></thead>
      <tbody>
        <tr><th scope="row">원문</th><td>행사 개요, 계획서에서 추출한 내용, 확인된 발언, 기존 보도자료 초안 등 작성·검토의 근거가 되는 자료</td></tr>
        <tr><th scope="row">작성 조건</th><td>기관 구분, 보도 시점, 작성 유형을 선택합니다. 공식 기관명·행사 시작일·행사 종료일·보도 희망일·탑재 예정 시각·대상·인원·기보도 자료·사진 여부·담당 부서·팀에는 확인된 값을 입력합니다.</td></tr>
        <tr><th scope="row">반려 사유 사전 점검</th><td>기보도, 50명 이하 교직원 연수·원격 연수, 직원 복지·친목 행사, 교직원 개인 수상, 단순 협약·견학·반복 회의, 협의 단계, 단순 강사 소개·단순 업무 수행, 타 기관 사업을 담당자가 ‘해당 / 비해당 / 미확인’으로 선택합니다. ‘해당’이 있으면 작성 보류를 검토합니다.</td></tr>
        <tr><th scope="row">작성 조건 점검</th><td>입력한 날짜로 탑재 기한(보도 희망일 전날 15시), 행사 당일·익일 보도, 3일 이상 경과 여부를 바로 계산해 표시합니다. 휴일과 행사 후 2일째는 담당자 확인으로 남기며, 같은 결과가 초안 생성 시 AI에 전달됩니다.</td></tr>
      </tbody>
    </table>
  </div>
  <div class="example">
    <p class="label">가상 입력 예시</p>
    <p><strong>원문</strong><br>11월 6일 중학생 75명을 대상으로 생활 안전 실습을 진행했다. 학생들은 대피 경로와 안전 수칙을 점검했다.</p>
    <p><strong>작성 조건</strong><br>기관 구분: 본청 / 보도 시점: 행사 완료<br>행사 시작일·종료일: 2026-11-06 / 대상·인원: 중학생 75명<br>공식 기관명 등 나머지 항목은 원자료로 확인한 값만 입력합니다.</p>
  </div>
  <h3>파일 열기와 끌어놓기</h3>
  <p><strong>파일 열기</strong>로 TXT·MD·HWPX·텍스트 PDF를 선택하거나, 파일 탐색기의 파일을 <strong>원문 입력칸</strong>으로 끌어다 놓습니다. 텍스트는 직접 붙여넣을 수도 있습니다. HWPX는 문단·표·글상자·각주의 글을 읽고, 숨은 설명과 메모는 읽지 않습니다.</p>
  <p>파일을 넣은 뒤 추출된 내용을 확인하세요. 이미지로만 된 PDF는 텍스트 추출이 어려울 수 있고 문서 구조에 따라 누락될 수 있습니다. 필요한 내용을 텍스트로 옮겨 보완합니다. 확인되지 않은 사실은 추측해서 채우지 말고 확인 필요로 남기세요.</p>
</section>

<section id="draft">
  <h2><small>04</small>초안 생성과 맞춤법 검사</h2>
  <p>기존 원고를 검토하려면 <strong>원문</strong>에 기존 초안을 넣고, <strong>작성 조건 → 작성 유형 → 기존 초안 검토</strong>를 선택한 뒤 <strong>초안 생성</strong>을 누릅니다. 맞춤법만 검사할 때는 기존 원고를 가운데 초안 입력칸에 붙여넣고 <strong>맞춤법 검사</strong>를 누르세요.</p>
  <ol>
    <li>원문과 작성 조건을 확인하고 <strong>초안 생성</strong>을 누릅니다.</li>
    <li>가운데 <strong>초안 작성·편집</strong>에 표시된 원고를 읽고 직접 편집합니다. 오른쪽 <strong>검토</strong> 영역의 분량·확인 안내도 함께 봅니다. 이 영역은 지침이 지양하는 표현(뜻깊·열띤·획기적·성공적으로 운영 등), 쉬운 우리말로 바꿀 외국어(매뉴얼·멘토·멘토링·프로젝트·TF·리더십·데이터·채널·플랫폼·콘텐츠 등, ‘안전매뉴얼’처럼 붙여 쓴 말 포함), ‘2026. 4. 18.’ 같은 공문서식 날짜도 표시합니다. 표시는 참고용이며 최종 판단은 담당자가 합니다.</li>
    <li><strong>맞춤법 검사</strong>를 누릅니다. 현재 편집 중인 초안을 선택한 AI가 검토합니다.</li>
    <li><strong>맞춤법 수정안 확인</strong>에서 원문·수정안·수정 이유를 비교한 뒤 <strong>수정안 적용</strong> 또는 <strong>적용하지 않기</strong>를 선택합니다.</li>
    <li>적용을 취소하려면 초안 입력칸에 포커스를 두고 <kbd>Ctrl</kbd> + <kbd>Z</kbd>를 누릅니다. 다른 편집 이후에는 직전 편집부터 차례로 되돌아갑니다.</li>
  </ol>
  <figure>
    <img src="@@PROOFREAD@@" alt="맞춤법 수정안 확인 창. 왼쪽 초안 원문과 오른쪽 수정안을 비교하고 하단에서 적용 여부를 선택한다." width="1024" height="701" loading="lazy">
    <figcaption>맞춤법 비교 화면 예시 — 수정안을 자동으로 확정하지 않고 사용자가 확인한 뒤 적용합니다.</figcaption>
  </figure>
  <aside class="note"><p><strong>최종 원고는 사람이 확인합니다.</strong> AI가 제시한 기관명·날짜·인원·장소·인용과 보도 시점을 원자료와 대조하세요. 맞춤법 수정안에서도 의미나 수치가 바뀌지 않았는지 확인합니다.</p></aside>
  <p>검사 중 초안이 바뀌어 적용할 수 없다는 안내가 나오면, 현재 초안을 기준으로 맞춤법 검사를 다시 실행하세요.</p>
</section>

<section id="save">
  <h2><small>05</small>TXT·MD·HWPX로 저장하기</h2>
  <ol>
    <li>하단 <strong>작업 폴더 ▾ → 폴더 지정</strong>에서 실제 저장할 폴더를 고릅니다.</li>
    <li><strong>TXT</strong>, <strong>MD</strong>, <strong>HWPX</strong> 중 필요한 형식을 체크합니다. 여러 형식을 함께 저장할 수도 있습니다.</li>
    <li><strong>초안 저장</strong>을 누르고 하단에 표시된 파일명·전체 저장 경로·완료 안내를 확인합니다.</li>
    <li><strong>작업 폴더 ▾ → 폴더 열기</strong>로 파일을 확인합니다.</li>
  </ol>
  <p>기본 파일명은 <code>날짜_보도자료_초안.txt</code>, <code>날짜_보도자료_초안.md</code>, <code>날짜_보도자료_초안.hwpx</code>입니다. 같은 이름이 있으면 새 이름으로 저장합니다. MD는 제목·강조 등 서식 기호를 유지하고 TXT는 해당 기호를 정리하므로 표시 형식에 차이가 있습니다.</p>
  <p><strong>HWPX</strong>는 인천광역시교육청 보도자료 양식에 제목·본문 문단·보도 희망일·제공일·사진 여부·담당 부서를 채운 파일입니다. 초안의 ‘## 보도자료 초안’ 아래 제목 한 줄과 본문 문단이 구분되어 있어야 하며, 작성 보류 초안은 양식으로 저장하지 않습니다. 보도 희망일을 입력하지 않으면 보도 시점과 제공일 칸은 빈칸으로 남으므로 한글에서 직접 기재하세요. 자료문의 이름·연락처는 한글에서 직접 기재하고, 누리집 탑재 전에 한글로 열어 서식을 확인하세요. 한글이 설치되지 않은 PC나 웹 미리보기에서는 양식 글꼴(휴먼명조·한양견고딕 등)이 다른 글꼴로 바뀌어 보일 수 있으며, 이는 교육청 원본 양식을 열 때와 같습니다.</p>
  <aside class="note"><p><strong>폴더를 여는 것만으로는 저장되지 않습니다.</strong> 초안을 고친 뒤에는 다시 <strong>초안 저장</strong>을 눌러야 합니다. 원자료와 편집 내용이 다음 실행에 자동 복원된다고 가정하지 말고, 앱을 닫기 전에 필요한 원고를 파일로 저장하세요.</p></aside>
  <p>저장이 실패하면 하단 오류 안내와 표시된 경로를 먼저 확인하고 쓰기 가능한 폴더를 다시 지정합니다. 생성된 파일을 확인하기 전에는 앱을 닫지 마세요.</p>
</section>

<section id="display">
  <h2><small>06</small>화면을 편하게 조절하기</h2>
  <p><strong>설정 → 화면 설정</strong>에서 <strong>라이트 모드</strong> 또는 <strong>다크 모드</strong>를 선택합니다. 글자 크기는 <strong>11~20포인트</strong>를 0.1포인트 단위로 조절하며 화면에 바로 반영됩니다.</p>
  <ul>
    <li>원자료와 초안 사이의 구분선을 끌어 입력칸 폭을 조절합니다. 구분선에 키보드 포커스를 둔 뒤 좌우 방향키를 사용할 수도 있습니다. 검토 칸은 머리글의 <strong>숨기기</strong>로 접고 오른쪽 가장자리의 <strong>검토 보기</strong>로 다시 폅니다. 접힘 상태는 다시 열었을 때 복원됩니다. 구분선을 마우스로 끈 뒤에는 포커스가 이전 입력칸으로 돌아가므로 방향키가 구분선을 움직이지 않습니다.</li>
    <li>글자 크기가 <strong>17포인트 이상</strong>이면 작업 버튼이 <strong>원자료 ▾</strong>와 <strong>초안 ▾</strong> 메뉴로 묶입니다. 작성 조건 탭은 <strong>조건</strong>으로 짧게 표시될 수 있습니다.</li>
    <li>화면 모드·글자 크기·편집 폭은 다시 실행했을 때 복원됩니다.</li>
  </ul>
</section>

<section id="update">
  <h2><small>07</small>자동 업데이트</h2>
  <p>앱은 시작할 때와 실행 중 6시간마다 공식 GitHub 릴리스 정보를 확인합니다. <strong>업데이트 확인</strong>으로 직접 확인할 수도 있습니다. 이 단계에서는 실행 파일을 내려받지 않습니다.</p>
  <ol>
    <li>새 버전이 있으면 버튼이 <strong>vX.Y.Z 내려받기</strong>로 바뀝니다. 누르면 실행 파일을 내려받아 검증하고 <strong>종료 시 적용</strong> 안내가 표시됩니다.</li>
    <li>작업 중인 초안을 저장하고 앱을 정상 종료합니다.</li>
    <li>앱을 다시 실행해 새 버전을 사용합니다.</li>
  </ol>
  <p>실행 파일이 있는 폴더에 쓰기 권한이 필요합니다. PC를 바로 끄면 준비된 파일은 적용되지 않고 삭제됩니다. 교체한 이전 실행 파일은 같은 폴더에 가장 최근 <code>.bak</code> 하나만 보관합니다. 자동 업데이트는 실행 파일에 적용되며 최신 안내 파일은 릴리스에서 별도로 내려받을 수 있습니다.</p>
</section>

<section id="help">
  <h2><small>08</small>문제가 생겼을 때</h2>
  <details open>
    <summary>로그인했는데 초안 생성이 안 됩니다.</summary>
    <p>상단에서 로그인한 AI를 선택했는지, <strong>외부 AI 전송 동의</strong>가 켜졌는지 확인하세요. 설정 → AI 연결에서 상태를 새로고침하고 하단의 실제 오류를 확인합니다. 인증·서비스 사용 권한·사용량 한도는 각각 확인이 필요할 수 있습니다.</p>
  </details>
  <details open>
    <summary>ChatGPT가 다른 프로그램에서는 연결되어 있습니다.</summary>
    <p>이 앱의 Codex는 앱 전용 프로필을 사용합니다. 설정 → AI 연결에서 별도로 로그인하세요. 브라우저 로그인이 어려우면 장치 로그인 방식을 사용합니다.</p>
  </details>
  <details open>
    <summary>Gemini API 키를 넣었는데 연결되지 않습니다.</summary>
    <p>앱의 키 입력칸과 <strong>연결 확인</strong>을 사용했는지 확인하세요. Google 프로젝트의 API 사용 권한·사용량 한도·과금 설정과 오류 안내를 확인합니다. 앱을 다시 실행했다면 키를 다시 입력해야 합니다.</p>
  </details>
  <details open>
    <summary>파일을 넣어도 원문이 비거나 일부가 빠집니다.</summary>
    <p>지원 형식인지 확인하고, 이미지 PDF는 필요한 글을 텍스트로 옮겨 넣으세요. HWPX·PDF에서 추출된 내용은 원문과 대조해 누락 부분을 보완합니다.</p>
  </details>
  <details open>
    <summary>작업 폴더가 비어 있습니다.</summary>
    <p>TXT·MD 형식을 선택하고 <strong>초안 저장</strong>을 눌렀는지 확인하세요. 하단에 표시된 실제 저장 경로가 지금 연 폴더와 같은지 대조합니다. 오류가 있으면 문구를 확인하고 저장 폴더를 다시 지정하세요.</p>
  </details>
  <details open>
    <summary>작은 창에서 버튼이 안 보입니다.</summary>
    <p>큰 글자 모드에서는 <strong>원자료 ▾</strong> 또는 <strong>초안 ▾</strong>를 펼쳐 보세요. 원자료와 초안 사이 구분선으로 폭을 조정하거나 설정 → 화면 설정에서 글자 크기를 조절할 수 있습니다.</p>
  </details>
</section>
<footer>
  <p>인천교육 보도자료 작성기 v0.1.18 사용 안내 · <a href="https://github.com/newskool4d-sketch/press-release-writer/releases/tag/v0.1.18">배포 파일과 릴리스 안내</a> · <a href="#main">맨 위로</a></p>
  <p>이 HTML은 그림·스타일을 포함하므로 내려받아 브라우저에서 바로 열 수 있습니다. 외부 링크와 AI 서비스 이용에는 인터넷 연결이 필요합니다. 화면 예시는 가상 자료이며 실제 보도자료가 아닙니다.</p>
</footer>
</main>
</div>
<script>
(() => {
  'use strict';
  const key = 'press-release-writer-guide-view-v1';
  const root = document.documentElement;
  const theme = document.getElementById('guide-theme');
  const size = document.getElementById('guide-size');
  const output = document.getElementById('guide-size-value');
  const status = document.getElementById('guide-status');
  const defaults = {theme: window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light', size:16};
  let view = {...defaults};
  const normalize = (value) => ({theme:value?.theme === 'dark' ? 'dark' : 'light', size:Number.isInteger(value?.size) && value.size >= 14 && value.size <= 22 ? value.size : 16});
  try { const stored = localStorage.getItem(key); if (stored) view = normalize(JSON.parse(stored)); } catch { /* Storage may be unavailable for local files. */ }
  function apply(save) {
    root.dataset.theme = view.theme;
    root.style.fontSize = view.size + 'px';
    theme.value = view.theme;
    size.value = String(view.size);
    output.value = view.size + 'px';
    if (save) {
      try { localStorage.setItem(key, JSON.stringify(view)); status.textContent = '보기 설정을 이 브라우저에 저장했습니다. 작성기 앱의 설정은 바뀌지 않습니다.'; }
      catch { status.textContent = '이 브라우저에서는 설정을 저장할 수 없습니다. 이번 열람에만 적용됩니다.'; }
    }
  }
  theme.addEventListener('change', () => { view = normalize({theme:theme.value, size:view.size}); apply(true); });
  size.addEventListener('input', () => { view = normalize({theme:view.theme, size:Number(size.value)}); apply(true); });
  document.getElementById('guide-reset').addEventListener('click', () => { view = {...defaults}; apply(true); });
  apply(false);
  document.querySelector('.reading-tools').hidden = false;
})();
</script>
</body>
</html>
'''

HTML = HTML.replace("@@CI@@", data_uri(ROOT / "source/Desktop/Assets/incheon-education-ci.jpg", "image/jpeg"))
HTML = HTML.replace("@@WORKSPACE@@", data_uri(SHOTS / "workspace-filled.png", "image/png"))
HTML = HTML.replace("@@PROOFREAD@@", data_uri(SHOTS / "proofreading.png", "image/png"))
script = re.search(r"<script>(.*?)</script>", HTML, re.S).group(1)
HTML = HTML.replace("@@SCRIPTHASH@@", base64.b64encode(hashlib.sha256(script.encode("utf-8")).digest()).decode("ascii"))
payload = HTML.encode("utf-8")
local = ROOT / "사용안내_v0.1.18.html"
release = ROOT / "artifacts/v0.1.18/release/PressReleaseWriter-Guide-ko.html"
release.parent.mkdir(parents=True, exist_ok=True)
local.write_bytes(payload)
release.write_bytes(payload)
print(json.dumps({"local":str(local),"release":str(release),"bytes":len(payload),"sha256":hashlib.sha256(payload).hexdigest()}, ensure_ascii=False))
