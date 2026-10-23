"""WeldTrace 합성 데이터 생성기.

KAMP 원본 데이터는 재배포 허용이 명시되어 있지 않아 저장소에 넣지 않는다.
대신 원본에서 측정한 통계(docs/analysis.md)를 흉내 낸 합성 데이터를 만든다.

원본과 같은 형식으로 출력한다 (컬럼명 앞 공백, WorkingTime 앞 공백까지 동일).
    PageNo, Speed, Length, RealPower, SetFrequency, SetDuty, SetPower, GateOnTime, WorkingTime

사용법:
    python tools/synth/generate.py --days 77 --seed 7 --out data/sample

출력:
    welding_<start>_<end>.csv   용접 기록 (원본 형식)
    labels.csv                  주입한 이상 정답 (row_index, label, kind)

표준 라이브러리만 쓴다 (설치 없이 실행 가능).
"""
from __future__ import annotations

import argparse
import csv
import random
from datetime import datetime, timedelta
from pathlib import Path

# 39스텝 용접 프로그램: 스텝(PageNo) -> 설정 출력(SetPower). 원본 3,481개 모듈에서 100% 동일.
PROGRAM = {}
for p in range(1, 13):
    PROGRAM[p] = 82 if p % 2 else 83
for p in range(13, 19):
    PROGRAM[p] = 38
for p in range(19, 31):
    PROGRAM[p] = 83 if p % 2 else 82
for p in range(31, 40):
    PROGRAM[p] = 38

# 설정 출력별 공정 파라미터와 기준기(1~2월) 실측 출력 평균·표준편차
RECIPE = {
    38: {"speed": 30, "length": 19.4, "gate": 650, "nominal": 692.0, "sd": 0.8},
    82: {"speed": 250, "length": 241.1, "gate": 1154, "nominal": 1699.0, "sd": 4.4},
    83: {"speed": 250, "length": 241.2, "gate": 1670, "nominal": 1721.0, "sd": 4.0},
}

# 원본에서 관측한 출력 열화 패턴 (같은 조건 용접의 월평균, docs/analysis.md 3절)
#   1~2월: 안정 → 3~5월: 월 약 -0.19% → 6~7월: 월 약 -0.38%로 가속
STABLE_DAYS = 50
EARLY_RATE = -0.000063   # 하루 -0.0063%
LATE_START = 140
LATE_RATE = -0.000127    # 하루 -0.0127%


def drift_factor(age_days: int) -> float:
    """시작 후 경과 일수에 따른 출력 배율 (1.0 = 기준기 공칭 출력)."""
    if age_days < STABLE_DAYS:
        return 1.0
    if age_days < LATE_START:
        return 1.0 + EARLY_RATE * (age_days - STABLE_DAYS)
    return 1.0 + EARLY_RATE * (LATE_START - STABLE_DAYS) + LATE_RATE * (age_days - LATE_START)

HEADER = [" PageNo", " Speed", " Length", " RealPower", " SetFrequency",
          " SetDuty", " SetPower", " GateOnTime", " WorkingTime"]


def weld_row(page, set_power, real_power, ts, recipe):
    return [page, recipe["speed"], recipe["length"], int(round(real_power)), 1000, 100,
            set_power, recipe["gate"], " " + ts.strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]]


def generate(days: int, seed: int, start: datetime, modules_per_day: int, production_probability: float = 0.55):
    rng = random.Random(seed)
    rows, labels = [], []
    day = start
    produced_days = 0
    while produced_days < days:
        # 주말 비가동. 평일도 원본처럼 띄엄띄엄 가동한다 (원본: 7개월 중 77일 가동, 주당 약 2.6일)
        if day.weekday() >= 5 or rng.random() > production_probability:
            day += timedelta(days=1)
            continue
        age = (day - start).days
        # 가동일마다 출력 수준이 조금씩 다르다 (원본 기준기의 일평균 표준편차 약 0.07%)
        drift = drift_factor(age) + rng.gauss(0, 0.0006)
        ts = day.replace(hour=9, minute=0, second=0) + timedelta(seconds=rng.uniform(0, 600))
        n_modules = max(1, int(rng.gauss(modules_per_day, 8)))
        for _ in range(n_modules):
            kind = None
            r = rng.random()
            if r < 0.010:
                kind = "POWER_DROP"       # 출력 급락 (원본 테스트 04일 유형)
            elif r < 0.018:
                kind = "RECIPE_DEVIATION"  # 스텝 14 설정 출력 35 (원본 테스트 03일 유형)
            elif r < 0.020:
                kind = "NO_OUTPUT"         # 레이저 미출력 (원본 학습 데이터에 1개 모듈)
            module_offset = rng.gauss(0, 0.0005)
            for page in range(1, 40):
                set_power = PROGRAM[page]
                recipe = RECIPE[set_power]
                real = recipe["nominal"] * (drift + module_offset) + rng.gauss(0, recipe["sd"])
                label = 0
                if kind == "POWER_DROP":
                    real *= rng.uniform(0.55, 0.72)
                    label = 1
                elif kind == "RECIPE_DEVIATION" and page == 14:
                    set_power = 35
                    real = recipe["nominal"] * drift * rng.uniform(0.90, 0.93)
                    label = 1
                elif kind == "NO_OUTPUT":
                    real = 0
                    label = 1
                rows.append(weld_row(page, set_power, real, ts, recipe))
                labels.append((len(rows) - 1, label, kind or ""))
                ts += timedelta(seconds=max(2.0, rng.gauss(3.0, 0.4)))
            ts += timedelta(seconds=rng.uniform(60, 500))  # 모듈 투입 대기
            if ts.hour >= 18:
                break
        day += timedelta(days=1)
        produced_days += 1
    inject_data_quality_issues(rows, labels, rng)
    return rows, labels


def inject_data_quality_issues(rows, labels, rng):
    """원본에서 실제로 관측한 데이터 품질 문제를 소량 재현한다."""
    # 1) 완전 중복 행 (수집 재전송)
    i = rng.randrange(100, len(rows) - 100)
    rows.insert(i + 1, list(rows[i]))
    labels.insert(i + 1, (None, labels[i][1], "DUPLICATE"))
    # 2) 시간 역전 1건 (원본 2022-06-08 사례)
    j = rng.randrange(100, len(rows) - 100)
    t = datetime.strptime(rows[j][8].strip(), "%Y-%m-%d %H:%M:%S.%f")
    rows[j][8] = " " + (t - timedelta(minutes=8)).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
    labels[j] = (None, labels[j][1], "TIME_REVERSAL")
    # 3) 결측 1건
    k = rng.randrange(100, len(rows) - 100)
    rows[k][3] = ""
    labels[k] = (None, labels[k][1], "MISSING_VALUE")
    for n, (_, label, kind) in enumerate(labels):
        labels[n] = (n, label, kind)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--days", type=int, default=77, help="가동일 수 (원본과 같은 77일)")
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--start", default="2026-01-05")
    ap.add_argument("--modules-per-day", type=int, default=45)
    ap.add_argument("--production-probability", type=float, default=0.55, help="평일 가동 확률")
    ap.add_argument("--out", default="data/sample")
    args = ap.parse_args()

    start = datetime.strptime(args.start, "%Y-%m-%d")
    rows, labels = generate(args.days, args.seed, start, args.modules_per_day, args.production_probability)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    first = rows[0][8].strip()[:10].replace("-", "")
    last = rows[-1][8].strip()[:10].replace("-", "")
    data_path = out / f"welding_{first}_{last}.csv"
    with data_path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(HEADER)
        w.writerows(rows)
    with (out / "labels.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["row_index", "label", "kind"])
        w.writerows(labels)
    print(f"{data_path}: {len(rows)} rows")


if __name__ == "__main__":
    main()
