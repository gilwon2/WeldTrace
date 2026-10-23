"""합성 샘플 전체 적재 테스트의 기대값을 C# 구현과 독립적으로 계산한다 (교차 검증용).

규칙은 docs/analysis.md와 같다. C# 코드를 보지 않고 규칙 문장만 보고 다시 구현했다.
사용법: python tools/reference/expected_counts.py data/sample/welding_*.csv.gz
"""
import csv
import gzip
import sys
from collections import Counter
from datetime import datetime

PROGRAM = {}
for p in range(1, 13):
    PROGRAM[p] = 82 if p % 2 else 83
for p in range(13, 19):
    PROGRAM[p] = 38
for p in range(19, 31):
    PROGRAM[p] = 83 if p % 2 else 82
for p in range(31, 40):
    PROGRAM[p] = 38
NOMINAL = {38: 692.0, 82: 1699.0, 83: 1721.0}
FIELDS = ["PageNo", "Speed", "Length", "RealPower", "SetFrequency", "SetDuty", "SetPower", "GateOnTime", "WorkingTime"]


def main(path):
    with gzip.open(path, "rt", encoding="utf-8") as f:
        reader = csv.reader(f)
        header = [h.strip() for h in next(reader)]
        raw = [[v.strip() for v in row] for row in reader if row]

    data_lines = len(raw)
    parsed = []
    p_errors = 0
    for row in raw:
        rec = dict(zip(header, row))
        if any(rec[k] == "" for k in FIELDS):
            p_errors += 1
            continue
        parsed.append(rec)

    # D1/D2: 시각 기준
    by_time = Counter(r["WorkingTime"] for r in parsed)
    seen, accepted, d1 = set(), [], 0
    for r in parsed:
        key = tuple(r[k] for k in FIELDS)
        if by_time[r["WorkingTime"]] > 1:
            if key in seen:
                d1 += 1
                continue
        seen.add(key)
        accepted.append(r)

    # 모듈 조립: 스텝 재시작 또는 30분 초과 공백
    modules, cur = [], []
    for r in accepted:
        t = datetime.strptime(r["WorkingTime"], "%Y-%m-%d %H:%M:%S.%f")
        page = int(r["PageNo"])
        if cur and (page <= cur[-1][0] or (t - cur[-1][1]).total_seconds() > 1800):
            modules.append(cur)
            cur = []
        cur.append((page, t, int(r["RealPower"]), int(r["SetPower"])))
    modules.append(cur)

    verdicts, faults = Counter(), Counter()
    for i, m in enumerate(modules):
        complete = [w[0] for w in m] == list(range(1, 40))
        is_last = i == len(modules) - 1
        if is_last and not complete:
            verdicts["OPEN"] += 1
            continue
        codes = []
        for page, _, real, set_power in m:
            ratio = real / NOMINAL[PROGRAM[page]]
            if real == 0:
                codes.append("NO_OUTPUT")
            elif ratio < 0.95:
                codes.append("POWER_DROP")
            if set_power != PROGRAM[page]:
                codes.append("RECIPE_DEVIATION")
        ng = any(c in ("NO_OUTPUT", "POWER_DROP") for c in codes)
        verdict = "HOLD" if not complete else "NG" if ng else "WARNING" if codes else "OK"
        verdicts[verdict] += 1
        if verdict == "NG":
            primary = next(c for c in ("NO_OUTPUT", "POWER_DROP", "RECIPE_DEVIATION") if c in codes)
            faults[primary] += 1

    print(f"data_lines={data_lines} p2={p_errors} d1={d1} accepted={len(accepted)}")
    print(f"modules={sum(verdicts.values())} verdicts={dict(verdicts)} ng_by_fault={dict(faults)}")


if __name__ == "__main__":
    main(sys.argv[1])
