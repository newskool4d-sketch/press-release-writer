"""Check the delivered HTML, never output embedded image bodies."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlsplit
import base64
import hashlib
import json
import struct
import re

ROOT = Path(__file__).resolve().parent.parent
path = ROOT / "사용안내_v0.1.13.html"
payload = path.read_bytes()
source = payload.decode("utf-8")

class GuideParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.ids = []
        self.links = []
        self.images = []
        self.resources = []
        self.stack = []
        self.errors = []
        self.lang = None
        self.sections = 0
        self.scripts = 0
    def handle_starttag(self, tag, attrs):
        attr = dict(attrs)
        if tag == "html": self.lang = attr.get("lang")
        if tag == "section": self.sections += 1
        if tag == "script": self.scripts += 1
        if "id" in attr: self.ids.append(attr["id"])
        if tag == "a": self.links.append(attr.get("href", ""))
        if tag == "img": self.images.append(attr)
        for name in ("src", "srcset", "poster", "action"):
            if name in attr and tag != "img": self.resources.append(attr[name])
        if tag == "link": self.resources.append(attr.get("href", ""))
        if any(k.startswith("on") for k in attr): self.errors.append("inline event handler")
        if tag not in {"area","base","br","col","embed","hr","img","input","link","meta","param","source","track","wbr"}:
            self.stack.append(tag)
    def handle_endtag(self, tag):
        if not self.stack or self.stack[-1] != tag:
            self.errors.append("unbalanced " + tag)
        else: self.stack.pop()

p = GuideParser()
p.feed(source)
assert not p.errors and not p.stack, (p.errors, p.stack)
assert p.lang == "ko" and p.sections == 8
assert len(set(p.ids)) == len(p.ids)
assert p.scripts == 1 and not p.resources
script = re.search(r"<script>(.*?)</script>", source, re.S).group(1)
digest = base64.b64encode(hashlib.sha256(script.encode('utf-8')).digest()).decode('ascii')
assert "script-src 'sha256-" + digest + "'" in source
assert "@@" not in source
assert not any(s in source for s in ("C:\\Users", "file://", "localhost", "127.0.0.1", "API_KEY=", "sk-proj-"))
for link in p.links:
    if link.startswith("#"):
        assert link[1:] in p.ids, link
    else:
        url = urlsplit(link)
        assert url.scheme == "https" and url.hostname in {"github.com", "aistudio.google.com"}, link

images = []
for img in p.images:
    assert img.get("alt") and img.get("width") and img.get("height")
    prefix, body = img["src"].split(",", 1)
    assert prefix in {"data:image/png;base64", "data:image/jpeg;base64"}
    data = base64.b64decode(body, validate=True)
    if prefix == "data:image/png;base64":
        assert data[:8] == b"\x89PNG\r\n\x1a\n"
        width, height = struct.unpack(">II", data[16:24])
        assert (width, height) == (int(img["width"]), int(img["height"])), (width, height)
    else:
        assert data[:3] == b"\xff\xd8\xff"
    images.append({"type":prefix,"bytes":len(data),"sha256":hashlib.sha256(data).hexdigest()})
assert len(images) == 3
assert "@media(max-width:480px)" in source and "@media print" in source
assert "Content-Security-Policy" in source
asset = ROOT / "artifacts/v0.1.13/release/PressReleaseWriter-Guide-ko.html"
assert asset.read_bytes() == payload
result = {
    "request":"사용방법에 대한 html 파일 별도 제작, 릴리스에 같이 첨부",
    "scope":"v0.1.13 오프라인 HTML 안내 생성과 릴리스 첨부. 작성 조건 점검·반려 사유 사전 점검·HWPX 양식 저장 안내 반영.",
    "status":"PASS",
    "file":str(path),"bytes":len(payload),"sha256":hashlib.sha256(payload).hexdigest(),
    "sections":p.sections,"links":len(p.links),"images":images,
    "checks":["UTF-8 Korean", "balanced markup", "unique IDs and internal anchors", "embedded images decoded", "PNG dimensions match", "single CSP-hashed local script; no automatic external resources", "release copy identical", "responsive and print styles present", "no local path or account identifiers"],
    "rendering":"NOT_RUN: static checks only; view the HTML in a browser separately",
}
record = ROOT / "artifacts/v0.1.13/verification/html-guide-check.json"
record.parent.mkdir(parents=True, exist_ok=True)
record.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps({k:v for k,v in result.items() if k not in {"request","scope","file","images","checks"}},ensure_ascii=False))
