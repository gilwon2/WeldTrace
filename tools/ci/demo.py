"""README용 시연 영상을 녹화한다 (CI에서 실행, 결과는 webm → ffmpeg로 GIF).

사용법:
  python tools/ci/demo.py live http://localhost:8080 out/   # 가상 용접기가 도는 동안: 모듈 수가 늘어나는 장면
  python tools/ci/demo.py tour http://localhost:8080 out/   # 전체 샘플 적재 후: 열화 감시 → 모듈 추적 → 수집 이력

화면 아래에 단계 설명 자막을 띄운다 (영상만 보고도 흐름을 알 수 있게).
"""
import shutil
import sys
from pathlib import Path

from playwright.sync_api import Page, sync_playwright

VIEWPORT = {"width": 1280, "height": 720}

CAPTION_JS = """
(text) => {
  let el = document.getElementById('demo-caption');
  if (!el) {
    el = document.createElement('div');
    el.id = 'demo-caption';
    el.style.cssText = 'position:fixed;left:50%;bottom:22px;transform:translateX(-50%);z-index:99999;' +
      'background:rgba(17,24,39,.92);color:#fff;padding:10px 20px;border-radius:10px;' +
      'font:600 17px "Noto Sans CJK KR","Noto Sans KR",sans-serif;box-shadow:0 6px 24px rgba(0,0,0,.25);' +
      'max-width:1100px;text-align:center;line-height:1.45;pointer-events:none';
    document.body.appendChild(el);
  }
  el.textContent = text;
}
"""

HIGHLIGHT_JS = """
(selector) => {
  const el = document.querySelector(selector);
  if (!el) return;
  el.style.transition = 'box-shadow .3s';
  el.style.boxShadow = '0 0 0 4px rgba(31,95,214,.55)';
  el.style.borderRadius = '8px';
}
"""


def caption(page: Page, text: str) -> None:
    page.evaluate(CAPTION_JS, text)


def highlight(page: Page, selector: str) -> None:
    page.evaluate(HIGHLIGHT_JS, selector)


def scroll(page: Page, pixels: int, steps: int = 12) -> None:
    for _ in range(steps):
        page.mouse.wheel(0, pixels / steps)
        page.wait_for_timeout(60)


def goto(page: Page, url: str, text: str) -> None:
    page.goto(url, wait_until="networkidle")
    page.wait_for_timeout(400)  # 차트 애니메이션
    caption(page, text)


def live(page: Page, base: str) -> None:
    text = "가상 용접기 → 수집기(5초 주기) → SQL Server · 새로고침할 때마다 판정된 모듈이 늘어납니다"
    goto(page, base + "/", text)
    page.wait_for_timeout(2500)
    for _ in range(4):
        page.wait_for_timeout(5000)
        page.reload(wait_until="networkidle")
        page.wait_for_timeout(300)
        caption(page, text)
        highlight(page, ".kpi")
        page.wait_for_timeout(1200)


def tour(page: Page, base: str) -> None:
    goto(page, base + "/", "합성 샘플 77가동일 전체 적재: 134,003회 용접 → 모듈 3,436개 판정")
    page.wait_for_timeout(2500)
    scroll(page, 520)
    caption(page, "가동일별 불량률과 평균 출력 비율 · 최근 불량 모듈")
    page.wait_for_timeout(2500)

    goto(page, base + "/Drift", "출력 열화 감시: 가동일 평균 출력 비율에 EWMA 관리도 → 판정에 걸리기 전에 경보")
    page.wait_for_timeout(3500)
    scroll(page, 480)
    caption(page, "CUSUM 관리도와 추세: 경고선(0.98)·판정선(0.95)에 닿는 예상일")
    page.wait_for_timeout(3000)

    goto(page, base + "/Modules", "모듈 추적: 배터리 모듈 1개 = 레이저 용접 39스텝")
    page.wait_for_timeout(1500)
    page.select_option("select[name=date]", "")  # 기본은 마지막 가동일이라 전체 기간으로 바꾼다
    page.select_option("select[name=verdict]", "NG")
    page.wait_for_timeout(700)
    page.click("form.filters button[type=submit]")
    page.wait_for_load_state("networkidle")
    caption(page, "불량(NG) 모듈만 조회")
    page.wait_for_timeout(2200)

    # 첫 번째 불량 모듈 상세로 이동 (링크를 강조한 뒤 이동)
    href = page.evaluate("() => document.querySelector(\"a[href^='/Modules/']:not([href*='?'])\")?.getAttribute('href')")
    if not href:
        raise SystemExit(f"불량 모듈 링크가 없습니다: {page.url}")
    highlight(page, f"a[href='{href}']")
    page.wait_for_timeout(900)
    goto(page, base + href, "39스텝 역추적: 어느 스텝에서 실측 출력이 공칭의 95% 아래로 떨어졌는지")
    page.wait_for_timeout(3000)
    scroll(page, 520)
    page.wait_for_timeout(2500)

    goto(page, base + "/Batches", "수집 이력: 파일 1개 = 배치 1개 · 같은 파일·겹치는 행은 다시 적재하지 않음")
    page.wait_for_timeout(2500)
    href = page.evaluate("() => document.querySelector(\"a[href^='/Batches?id=']\")?.getAttribute('href')")
    if href:
        highlight(page, f"a[href='{href}']")
        page.wait_for_timeout(900)
        goto(page, base + href, "제외·경고한 행은 줄 번호와 사유로 기록 (예: 중복 D1 · 결측 P2 · 시간 역전 T1)")
        page.wait_for_timeout(3500)


def main(mode: str, base: str, out: str) -> None:
    out_dir = Path(out)
    out_dir.mkdir(parents=True, exist_ok=True)
    raw = out_dir / f"raw-{mode}"
    with sync_playwright() as p:
        browser = p.chromium.launch()
        context = browser.new_context(viewport=VIEWPORT, locale="ko-KR",
                                      record_video_dir=str(raw), record_video_size=VIEWPORT)
        page = context.new_page()
        {"live": live, "tour": tour}[mode](page, base)
        video = page.video
        context.close()  # 닫아야 영상 파일이 완성된다
        browser.close()
        shutil.move(video.path(), out_dir / f"{mode}.webm")
    shutil.rmtree(raw, ignore_errors=True)
    print(f"{mode}: {out_dir / (mode + '.webm')}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2].rstrip("/"), sys.argv[3])
