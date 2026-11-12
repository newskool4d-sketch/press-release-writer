# 인천교육 보도자료 작성기 MVP 0.1.17

Windows 11 x64용 로컬 WPF 앱입니다. 상단 **설정 → AI 연결**에서 Claude Code, Codex, Gemini의 상태 및 외부 AI 전송 동의를 관리합니다. **설정 → 화면 설정**에서 라이트·다크 모드와 11~20포인트 글자 크기를 선택합니다. 원자료와 초안 사이 구분선을 끌어 폭을 조절할 수 있습니다.

사용 절차는 [0.1.17 사용 안내](../사용안내_v0.1.17.md)를 참고하세요. 실행 파일 아이콘과 화면 도안은 `Desktop/Assets`에 포함돼 있습니다.

상단 **도움말 → 사용 안내**는 앱에 포함된 HTML 안내서를 기본 브라우저로 엽니다. 인터넷 없이 읽을 수 있으며 안내서에서 라이트·다크 모드와 글자 크기를 조절할 수 있습니다. 안내 파일은 앱 데이터 폴더의 `help`에 추출되고 앱을 업데이트하면 포함된 내용으로 갱신됩니다.

## 연결 방식

| 연결 | 인증과 작성 |
|---|---|
| Claude Code | 이 PC에 설치된 공식 CLI와 기존 Claude Code 로그인을 사용합니다. CLI가 표시하는 공식 로그인 절차를 따릅니다. |
| ChatGPT · Codex | 공식 app-server로 구독 계정 확인과 로그인, 초안 생성과 맞춤법 검사를 처리합니다. 앱 전용 `CODEX_HOME`을 사용합니다. |
| Gemini API | Google AI Studio에서 발급한 키를 현재 앱 실행 중에 사용합니다. 연결 확인은 모델 정보를 조회하며, 초안 작성과 맞춤법 검사는 Gemini Developer API의 `generateContent`를 사용합니다. |
| Gemini CLI | 학교·기관용 Gemini Code Assist 계정과 Google Cloud 프로젝트 ID를 사용합니다. 앱 전용 프로필에서 공식 CLI로 로그인합니다. 설치 여부는 상태 조회에서, 계정 이용 권한은 실제 요청에서 확인합니다. |

연결 상태와 외부 AI 전송 동의를 별도로 관리합니다. 동의는 앱을 열 때마다 꺼져 있고, 초안 생성과 맞춤법 검사에는 동의와 유효한 계정 연결이 필요합니다. 앱은 계정 비밀번호나 토큰 파일을 직접 읽거나 저장하지 않습니다. Claude Code 로그아웃은 공유되는 CLI 계정에도 적용됩니다.

이 연결은 로컬 앱용입니다. 호스팅·상업 서비스로 전환할 경우 제공사의 인증 허용 범위를 다시 검토해야 합니다. 실제 계정 로그인 완료·기관망·AI 생성은 자동 테스트만으로 검증되지 않습니다.

## 작성과 검토

- 기관 구분·공식 기관명·행사 상태·날짜·탑재 시각·대상 인원·기보도 확인 자료를 프롬프트에 전달합니다.
- 기준의 출처 표기는 `Writing.GuidelineReference`에서 관리합니다. 소통협력담당관-2207의 시행일은 원문 대조가 필요하므로 확정하지 않았습니다.
- 원자료 속 명령을 실행하지 않는 지시, 사실 충돌·정보 부족 처리, 공개 직무상 인명과 개인 식별정보 구분, 반려 검토의 미확인 상태를 보강했습니다.
- 앱은 분량·리드 길이·첫 인공지능(AI) 표기를 검사합니다. 사실 확인·반려 판단·휴일 예외는 제공 자료와 담당자 확인이 필요합니다.
- 원자료나 확인 정보를 수정한 뒤 닫으면 확인창을 표시합니다. 작업 중 종료는 취소·프로세스 정리를 기다립니다. 원자료는 자동 저장하지 않습니다.
- 맞춤법 메뉴는 현재 초안에서 오타·띄어쓰기 수정안을 요청합니다. 원문에 유일하게 대응하는 수정 구간만 수용하고 숫자·직접 인용 변경을 거부합니다. 수정 전후를 확인한 뒤 적용하며 Ctrl+Z로 되돌릴 수 있습니다.
- TXT·MD·HWPX·텍스트 PDF를 입력하고 TXT·MD와 공식 양식 HWPX로 저장합니다. 양식은 `Core/Assets/press-release-form.hwpx`(인천광역시교육청 보도자료 양식)를 내장해 머리말과 본문 자리만 바꿉니다. 파일 25MB, PDF 100쪽, 원자료 50,000자 제한과 저장 이름 충돌 회피를 유지합니다.

## 소스 구성

- `Core`: 파일 추출, 프롬프트·검토·저장, CLI 실행과 오류 분류, Codex app-server 계정 클라이언트
- `Desktop`: WPF 화면과 작업·종료 수명주기
- `Smoke`: 작성·파일·모의 app-server·Windows 프로세스 검사
- `DesktopSmoke`: WPF 컴포넌트 종료·취소·입력 보존·배치 검사
- `packaging`: NSIS 설치 정의와 아이콘 변환 스크립트

## 재현 검증과 실행 파일 생성

.NET 10 SDK가 필요합니다. 프로젝트 루트에서 실행합니다.

```powershell
.\source\verify.ps1 -DotnetPath .\.tools\dotnet\dotnet.exe -ProbeCodex -Publish
```

시스템 SDK가 있으면 `-DotnetPath`를 생략할 수 있습니다. NuGet 패키지 복원에는 네트워크가 필요합니다. 검증 스크립트는 SDK 상태·패키지를 프로젝트 `.tools`에 두고 SDK의 개발용 인증서 자동 생성을 끕니다.

`-ProbeCodex`는 설치된 공식 CLI를 빈 앱 전용 `CODEX_HOME`으로 실행해 초기 연결과 미로그인 계정 조회를 검사합니다. `-Publish`는 런타임을 포함한 x64 단일 실행 파일과 고지·사용 안내를 `artifacts/v0.1.17/portable`에 만듭니다.

선택한 한글 PDF의 추출 검사는 별도로 실행할 수 있습니다.

```powershell
.\.tools\dotnet\dotnet.exe .\source\Smoke\bin\Debug\net10.0\Smoke.dll C:\검증자료\한글.pdf
```

PDF를 제공하지 않으면 PDF 항목은 통과로 처리하지 않고 SKIP으로 표시합니다. WPF 컴포넌트 검사는 실제 화면 배치·브라우저 OAuth·설치·제거 검사를 대신하지 않습니다.

NSIS 설치 패키지는 필요할 때 별도로 빌드합니다. 경로는 절대 경로를 권장합니다.

```powershell
makensis /DVERSION=0.1.17 /DOUTPUT=C:\출력\PressReleaseWriter_Setup_v0.1.17.exe /DPAYLOAD=C:\프로젝트\artifacts\v0.1.17\portable .\source\packaging\Setup.nsi
```

## 공개 배포와 자동 업데이트

공개 배포 저장소는 [newskool4d-sketch/press-release-writer](https://github.com/newskool4d-sketch/press-release-writer/releases/latest)입니다. 새 버전은 `Desktop.csproj`의 버전을 올려 검증·빌드한 뒤 `source/packaging/Package-GitHubRelease.ps1`로 ZIP과 자산 해시를 만듭니다. 정식 `vX.Y.Z` 릴리스에 `PressReleaseWriter.exe`와 `PressReleaseWriter-win-x64.zip`을 올리고, 자산 해시를 확인한 뒤 공개합니다. 이미 공개한 버전의 실행 파일을 교체할 때는 버전 번호를 올려 새 릴리스로 게시합니다.

앱은 시작할 때 정식 최신 릴리스 정보만 확인하고, 사용자가 **vX.Y.Z 내려받기**를 누르면 실행 파일 이름, 크기, SHA-256과 제품 버전을 확인한 뒤 정상 종료할 때 실행 파일을 교체합니다. Windows 종료·로그오프 중에는 준비된 파일을 적용하지 않고 폐기합니다. 파일 교체는 쓰기 가능한 휴대용 폴더에서 동작하며 가장 최근 이전 파일 하나만 백업으로 남깁니다. `source/packaging/Test-PortableUpdate.ps1`은 이전·새 실행 파일을 별도 폴더에 복사해 교체와 재실행을 검증합니다.

## 공식 참고 자료

- [Codex app-server 인증](https://learn.chatgpt.com/docs/app-server#auth-endpoints)
- [Codex 비대화식 실행](https://learn.chatgpt.com/docs/non-interactive-mode)
- [Claude 인증 사용 정책](https://code.claude.com/docs/en/legal-and-compliance)
- [Gemini CLI 계정과 프로젝트 설정](https://geminicli.com/docs/get-started/authentication/)
- [Gemini API 키 발급과 프로젝트 설정](https://ai.google.dev/gemini-api/docs/api-key)
- [Google의 개인용 Gemini CLI 전환 안내](https://developers.googleblog.com/an-important-update-transitioning-gemini-cli-to-antigravity-cli/)
