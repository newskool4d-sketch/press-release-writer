# 인천교육 보도자료 작성기

행사 자료로 보도자료 초안을 만들고, 맞춤법을 검토해 TXT와 Markdown으로 저장하는 Windows x64 앱입니다.

[최신 버전 다운로드](https://github.com/newskool4d-sketch/press-release-writer/releases/latest)

릴리스의 `PressReleaseWriter-win-x64.zip`을 받아 쓰기 가능한 폴더에 압축을 풀고 `PressReleaseWriter.exe`를 실행하세요. ZIP에는 사용 안내와 라이선스 고지가 들어 있습니다. 실행 파일은 서명되지 않은 휴대용 빌드입니다.

- **설정 → AI 연결**에서 Claude Code, Codex, Gemini API를 사용할 수 있습니다. 기관용 Gemini CLI 설정도 제공됩니다.
- **설정 → 화면 설정**에서 라이트·다크 모드와 11~20포인트 글자 크기를 선택하고, 편집칸 사이 구분선으로 원자료와 초안의 폭을 조절합니다.
- **초안 생성**과 **맞춤법 검사**에는 외부 AI 전송 동의가 필요합니다.
- 앱이 새 버전을 내려받으면 **종료 시 적용**으로 표시됩니다. 작업을 저장하고 정상 종료한 뒤 다시 열면 새 버전을 사용합니다.

소스 빌드와 검증 방법은 [소스 안내](source/README.md), 사용 절차는 [v0.1.11 사용 안내](사용안내_v0.1.11.md)에 있습니다.

## HTML 사용 안내

[HTML 안내서 다운로드](https://github.com/newskool4d-sketch/press-release-writer/releases/download/v0.1.11/PressReleaseWriter-Guide-ko.html) 후 브라우저에서 열어 주세요. 그림과 스타일을 포함한 단일 파일이며 상단에서 라이트·다크 모드와 글자 크기(14~22px)를 조절할 수 있습니다. 보기 설정은 브라우저에 저장됩니다.

저장소의 `사용안내_v0.1.11.html`도 같은 파일입니다. 아래 명령은 저장소 루트에서 실행합니다. Python 3와 Node.js가 필요합니다.

```text
python scripts/build_html_guide.py
python scripts/verify_html_guide.py
node scripts/check_guide_controls.cjs
```

제작 스크립트는 `docs/images`의 가상 자료 화면과 기존 기관 도안을 HTML 안에 포함합니다. 릴리스용 사본은 `artifacts/v0.1.11/release/PressReleaseWriter-Guide-ko.html`에 생성합니다. 검증은 HTML 구조·내장 이미지·CSP와 보기 설정 로직을 확인하며, 실제 브라우저 렌더링은 별도로 확인합니다.
