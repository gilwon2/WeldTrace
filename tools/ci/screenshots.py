"""실행 중인 WeldTrace 웹 화면을 캡처한다 (README용, CI에서 실행).

사용법: python tools/ci/screenshots.py http://localhost:8080 out/
"""
import re
import sys
import urllib.request
from pathlib import Path

from playwright.sync_api import sync_playwright


def first_ng_module(base: str) -> str | None:
    html = urllib.request.urlopen(f"{base}/Modules?verdict=NG").read().decode("utf-8")
    match = re.search(r'href="/Modules/(\d+)"', html)
    return match.group(1) if match else None


def main(base: str, out: str) -> None:
    out_dir = Path(out)
    out_dir.mkdir(parents=True, exist_ok=True)
    pages = {
        "home": "/",
        "drift": "/Drift",
        "modules": "/Modules?verdict=NG",
        "batches": "/Batches",
    }
    ng = first_ng_module(base)
    if ng:
        pages["module-detail"] = f"/Modules/{ng}"

    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 1366, "height": 900}, device_scale_factor=1, locale="ko-KR")
        for name, path in pages.items():
            page.goto(base + path, wait_until="networkidle")
            page.wait_for_timeout(500)  # 차트 그리기
            page.screenshot(path=str(out_dir / f"{name}.png"), full_page=True)
            print(f"{name}: {path}")
        browser.close()


if __name__ == "__main__":
    main(sys.argv[1].rstrip("/"), sys.argv[2])
