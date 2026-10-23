# WeldTrace

> 전기차 배터리 모듈 **레이저 용접 라인의 MES·설비 데이터 시스템** (C# .NET 8 · SQL Server)
> 용접기 데이터를 주기적으로 수집·검증해 모듈 단위로 추적하고, 출력 이상과 **레이저 출력의 점진적 열화**를 잡아냅니다.

`docker compose up -d --build` 한 줄로 SQL Server · 가상 용접기 · 수집기 · 웹 화면이 함께 뜹니다 → http://localhost:8080

![현황 화면](https://raw.githubusercontent.com/gilwon2/WeldTrace/screenshots/home.png)

| 지표 | 값 |
|---|---|
| 테스트 | 100개 통과 (단위 69 + SQL Server·웹 통합 31) |
| 빌드 경고 | 0 |
| 대량 적재 | 합성 샘플 134,003행 · 3,436모듈 (CI, SQL Server 2022 컨테이너) |
| 종단 테스트 | CI에서 docker compose로 전체를 띄워 수집 → 판정 → 웹 화면·API까지 확인, 화면 캡처 자동 갱신 |
| 교차 검증 | 샘플 전체 판정 결과가 독립 Python 구현과 일치, 조각 수집 결과가 일괄 적재와 일치 |

## 왜 만들었나

실제 공장 데이터(KAMP 배터리팩 용접, 7개월 13만 5천 회 용접)를 분석해 보니 두 가지가 보였습니다. ([분석 문서](docs/analysis.md))

1. **라벨된 불량은 규칙만으로 모두 잡힌다.** 스텝 공칭 출력 대비 95% 미만 규칙으로 이상 용접 292건을 전부 검출했고, 7개월 정상 용접에서 오경보는 0건이었습니다.
2. **진짜 문제는 서서히 떨어지는 레이저 출력이다.** 같은 조건의 용접 출력이 7개월간 약 1.3% 떨어졌습니다. 가동일 단위 EWMA 관리도는 출력이 0.3% 계단식으로 떨어진 **2022-03-17 당일**에 경보를 냈습니다. 판정 기준(95%)으로는 모든 용접이 합격이던 시점입니다.

그래서 WeldTrace는 "불량을 맞히는 모델"보다 **"설비 데이터를 믿을 수 있게 받아서, 모듈 단위로 추적하고, 열화를 일찍 알리는 시스템"**에 집중합니다.

## 구성

```
용접기(리플레이) ──3초──▶ 수집기 ──▶ 검증 ──▶ SQL Server ──▶ 모듈 판정 · 관리도 ──▶ 대시보드 / 알림
                                  (P·D·R·T 규칙)   (레시피 마스터,           (EWMA · CUSUM ·         │
                                                     모듈 추적, 이력)          추세 예측)              ▼
                                                                                         FactoryLens (불량률 분석)
```

| 영역 | 내용 | 상태 |
|---|---|---|
| 도메인 (`WeldTrace.Core`) | 39스텝 레시피, CSV 파싱, 검증 규칙, 모듈 조립, 판정, EWMA·CUSUM, 추세 예측, FactoryLens 내보내기 | ✅ |
| 저장 (`WeldTrace.Data`) | SQL Server 스키마·마이그레이션, 멱등 적재(K1 파일·K2 행), 배치 이력, 열린 모듈, 품질·역추적 뷰 | ✅ |
| 수집 (`WeldTrace.Collector`) | .NET Worker Service: inbox 주기 처리, 결과별 보관, 시작 시 DB 생성·마이그레이션 | ✅ |
| 시뮬레이터 (`WeldTrace.Simulator`) | 기록 데이터를 배속 재생해 조각 파일로 내려놓는 가상 용접기 | ✅ |
| 화면 (`WeldTrace.Web`) | ASP.NET Core Razor Pages: 현황, 출력 열화 관리도, 모듈 추적, 수집 이력 + JSON·CSV API | ✅ |

## 화면

| 출력 열화 감시 | 모듈 추적 (39스텝) |
|---|---|
| ![출력 열화](https://raw.githubusercontent.com/gilwon2/WeldTrace/screenshots/drift.png) | ![모듈 상세](https://raw.githubusercontent.com/gilwon2/WeldTrace/screenshots/module-detail.png) |

- **출력 열화**: 가동일 평균 출력 비율에 EWMA·CUSUM 관리도. 회색은 기준 구간(첫 10가동일), 빨간 점은 경보일. 직선 추세로 0.98·0.95 도달 예상일을 표시합니다.
- **모듈 추적**: 모듈 하나의 39회 용접을 스텝별 설정·실측 출력과 결함으로 역추적합니다 (MES 추적성).
- **수집 이력**: 파일(배치)별 읽음·적재·제외·기존 건수와 데이터 이슈. 겹치는 파일을 넣으면 "기존" 열에 K2가 건너뛴 행 수가 보입니다.
- 캡처는 CI가 77가동일 합성 샘플을 적재한 뒤 매번 새로 찍습니다.

## 판정 규칙 요약

| 코드 | 의미 | 결과 |
|---|---|---|
| `NoOutput` | 실측 출력 0 | 불량 |
| `PowerDrop` | 실측 / 스텝 공칭 출력 < 0.95 | 불량 |
| `RecipeDeviation` | 설정 출력이 스텝 지정값과 다름 | 경고 (공정 이탈 기록) |

설정 이탈을 불량으로 보지 않는 이유: 원본에서 설정이 바뀌어도 출력이 정상이면 검사에 합격했습니다. ([근거](docs/analysis.md#5-공정-이탈--불량))

## 실행

전체 실행 (Docker Desktop 필요): SQL Server + 가상 용접기 + 수집기

```bash
docker compose up -d --build
# 브라우저에서 http://localhost:8080
docker compose logs -f collector     # 5초마다 조각 파일을 적재하는 로그
```

시뮬레이터는 합성 샘플을 3600배속(실제 1초 = 설비 1시간)으로 재생합니다.

테스트:

```bash
dotnet test WeldTrace.sln          # 단위 테스트 (SQL Server 통합 테스트는 자동으로 건너뜀)
```

SQL Server 통합 테스트까지 돌리려면:

```bash
docker compose up -d sqlserver
# PowerShell: $env:WELDTRACE_TEST_SQL="Server=localhost,1433;User Id=sa;Password=WeldTrace!Local2026;TrustServerCertificate=True;Encrypt=False"
export WELDTRACE_TEST_SQL="Server=localhost,1433;User Id=sa;Password=WeldTrace!Local2026;TrustServerCertificate=True;Encrypt=False"
dotnet test WeldTrace.sln
```

규칙 전체: [docs/rules.md](docs/rules.md)

합성 데이터 다시 만들기 (표준 라이브러리만 사용):

```bash
python tools/synth/generate.py --days 60 --seed 7 --out data/sample
```

## 데이터

원본은 KAMP에서 받을 수 있으며, 재배포 허용이 명시되지 않아 저장소에 넣지 않았습니다.
`data/sample/`은 원본 통계(공칭 출력, 표준편차, 열화 속도, 39스텝 구조, 이상 유형)를 흉내 낸 **합성 데이터**입니다.

> 출처: 중소벤처기업부, Korea AI Manufacturing Platform(KAMP), 전자부품(배터리팩) 예지보전 AI 데이터셋,
> 스마트제조혁신추진단(㈜인터엑스, 네스트필드㈜), 2022.12.23., www.kamp-ai.kr

## 진행 현황

- [x] 실데이터 분석과 설계 근거 정리
- [x] 합성 데이터 생성기
- [x] Core: 파싱·검증·모듈 조립·판정·관리도·추세·내보내기 + 단위 테스트
- [x] SQL Server 스키마와 적재 (멱등, 배치 이력, 열린 모듈)
- [x] 10만 행 규모 테스트와 독립 구현 교차 검증
- [x] 수집기 (Worker Service)와 리플레이 시뮬레이터
- [x] Docker Compose와 CI 종단 테스트
- [x] 모니터링 화면과 API, CI 화면 캡처
- [ ] 기술 선택 트레이드오프 표, 면접 대비 노트

## 관련 프로젝트

- [FactoryLens](https://github.com/gilwon2/FactoryLens): 생산·검사 데이터 품질 분석 (Python). WeldTrace의 모듈 판정 결과를 그대로 받아 불량률·관리도를 그립니다.
