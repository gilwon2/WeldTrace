"""CI 결과 요약: TRX 테스트 결과와 Cobertura 커버리지를 GitHub 주석·요약으로 남긴다.

사용법: python3 tools/ci/summarize.py TestResults
"""
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main(root: str) -> int:
    base = Path(root)
    trx_files = list(base.rglob("*.trx"))
    if not trx_files:
        print("::error::테스트 결과(TRX)가 없습니다.")
        return 1

    total = passed = failed = skipped = 0
    for trx in trx_files:
        tree = ET.parse(trx)
        counters = tree.find(".//t:ResultSummary/t:Counters", NS)
        if counters is not None:
            total += int(counters.get("total", 0))
            passed += int(counters.get("passed", 0))
            failed += int(counters.get("failed", 0))
        for result in tree.iterfind(".//t:UnitTestResult", NS):
            if result.get("outcome") == "NotExecuted":
                skipped += 1
            if result.get("outcome") == "Failed":
                message = result.findtext(".//t:Message", default="", namespaces=NS).strip().replace("\n", " ")
                print(f"::error title={result.get('testName')}::{message[:900]}")

    covered = valid = 0
    for cov in base.rglob("coverage.cobertura.xml"):
        root = ET.parse(cov).getroot()
        covered += int(root.get("lines-covered", 0))
        valid += int(root.get("lines-valid", 0))
    line_rate = covered / valid if valid else None

    coverage = f"{line_rate * 100:.1f}%" if line_rate is not None else "n/a"
    summary = f"Tests: {passed}/{total} passed, {failed} failed, {skipped} skipped ({len(trx_files)} projects). Line coverage: {coverage}"
    print(f"::notice::{summary}")
    perf = base / "perf.txt"
    if perf.exists():
        for line in perf.read_text(encoding="utf-8").splitlines():
            print(f"::notice::{line}")
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as f:
            f.write(f"### {summary}\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "TestResults"))
