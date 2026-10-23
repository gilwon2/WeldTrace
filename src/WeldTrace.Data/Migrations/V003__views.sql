-- 조회용 뷰. 화면·API·FactoryLens 내보내기가 같은 정의를 쓰도록 DB에 둔다.

-- 작업일별 품질 요약 (판정 보류 HOLD 제외)
CREATE OR ALTER VIEW dbo.vw_daily_quality
AS
SELECT m.equipment_id,
       CAST(m.start_time AS date)                                   AS work_date,
       COUNT(*)                                                     AS modules,
       SUM(CASE WHEN m.verdict = 'NG' THEN 1 ELSE 0 END)            AS ng_modules,
       SUM(CASE WHEN m.verdict = 'WARNING' THEN 1 ELSE 0 END)       AS warning_modules,
       CAST(SUM(CASE WHEN m.verdict = 'NG' THEN 1 ELSE 0 END) AS float) / COUNT(*) AS ng_rate,
       AVG(m.mean_power_ratio)                                      AS mean_power_ratio
FROM dbo.module AS m
WHERE m.verdict <> 'HOLD'
GROUP BY m.equipment_id, CAST(m.start_time AS date);
GO

-- 모듈 역추적: 모듈 하나의 모든 용접과 결함 (MES 추적성)
CREATE OR ALTER VIEW dbo.vw_module_trace
AS
SELECT m.module_id,
       m.equipment_id,
       m.verdict,
       m.primary_fault,
       w.weld_id,
       w.page_no,
       w.working_time,
       w.set_power,
       s.set_power                                  AS expected_set_power,
       w.real_power,
       w.power_ratio,
       w.batch_id,
       STRING_AGG(f.fault_code, ',') WITHIN GROUP (ORDER BY f.fault_code) AS faults
FROM dbo.module AS m
JOIN dbo.weld AS w
  ON w.module_id = m.module_id
JOIN dbo.recipe_step AS s
  ON s.recipe_code = m.recipe_code AND s.page_no = w.page_no
LEFT JOIN dbo.weld_fault AS f
  ON f.weld_id = w.weld_id
GROUP BY m.module_id, m.equipment_id, m.verdict, m.primary_fault,
         w.weld_id, w.page_no, w.working_time, w.set_power, s.set_power,
         w.real_power, w.power_ratio, w.batch_id;
