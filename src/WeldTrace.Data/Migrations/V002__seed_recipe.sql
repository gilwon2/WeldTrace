-- 기본 레시피(BM-LASER-39)와 설비(LW-01)
-- 스텝 구성은 KAMP 학습 데이터 3,481개 모듈에서 100% 동일했던 패턴이다 (docs/analysis.md)
--   1~12: 82/83 교대(홀수 82), 13~18: 38, 19~30: 83/82 교대(홀수 83), 31~39: 38

INSERT INTO dbo.recipe (recipe_code, step_count, description)
VALUES ('BM-LASER-39', 39, N'배터리 모듈 레이저 용접 39스텝');

WITH steps AS (
    SELECT TOP (39) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS page_no
    FROM sys.all_objects
)
INSERT INTO dbo.recipe_step (recipe_code, page_no, set_power)
SELECT 'BM-LASER-39',
       page_no,
       CASE
           WHEN page_no <= 12 THEN CASE WHEN page_no % 2 = 1 THEN 82 ELSE 83 END
           WHEN page_no <= 18 THEN 38
           WHEN page_no <= 30 THEN CASE WHEN page_no % 2 = 1 THEN 83 ELSE 82 END
           ELSE 38
       END
FROM steps;

-- 공칭 출력: 기준기(첫 200개 모듈) 실측 평균
INSERT INTO dbo.recipe_power (recipe_code, set_power, nominal_w)
VALUES ('BM-LASER-39', 38, 692.0),
       ('BM-LASER-39', 82, 1699.0),
       ('BM-LASER-39', 83, 1721.0);

INSERT INTO dbo.equipment (equipment_id, name, recipe_code)
VALUES ('LW-01', N'레이저 용접기 1호기', 'BM-LASER-39');
