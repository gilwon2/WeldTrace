-- WeldTrace 스키마 v1
-- 마스터(레시피·설비) → 수집 이력(배치·이슈) → 실적(용접·모듈·결함) 순서로 만든다.
-- 멱등성 키
--   K1 파일: 같은 설비에 같은 내용(SHA-256)의 파일이 이미 성공 적재되었으면 건너뜀 (ux_ingest_batch_source)
--   K2 행:   같은 설비·같은 용접 시각의 행은 한 번만 저장 (uq_weld_equipment_time)

CREATE TABLE dbo.recipe (
    recipe_code  varchar(30)   NOT NULL CONSTRAINT pk_recipe PRIMARY KEY,
    step_count   int           NOT NULL CONSTRAINT ck_recipe_step_count CHECK (step_count > 0),
    description  nvarchar(200) NULL,
    created_at   datetime2(0)  NOT NULL CONSTRAINT df_recipe_created_at DEFAULT sysutcdatetime()
);

-- 스텝별 설정 출력 (모듈 1개 = 스텝 1..step_count)
CREATE TABLE dbo.recipe_step (
    recipe_code  varchar(30) NOT NULL CONSTRAINT fk_recipe_step_recipe REFERENCES dbo.recipe (recipe_code),
    page_no      int         NOT NULL CONSTRAINT ck_recipe_step_page CHECK (page_no >= 1),
    set_power    int         NOT NULL,
    CONSTRAINT pk_recipe_step PRIMARY KEY (recipe_code, page_no)
);

-- 설정 출력별 공칭 출력 (판정 비율의 분모)
CREATE TABLE dbo.recipe_power (
    recipe_code  varchar(30)  NOT NULL CONSTRAINT fk_recipe_power_recipe REFERENCES dbo.recipe (recipe_code),
    set_power    int          NOT NULL,
    nominal_w    decimal(7,1) NOT NULL CONSTRAINT ck_recipe_power_nominal CHECK (nominal_w > 0),
    CONSTRAINT pk_recipe_power PRIMARY KEY (recipe_code, set_power)
);

CREATE TABLE dbo.equipment (
    equipment_id varchar(20)   NOT NULL CONSTRAINT pk_equipment PRIMARY KEY,
    name         nvarchar(100) NOT NULL,
    recipe_code  varchar(30)   NOT NULL CONSTRAINT fk_equipment_recipe REFERENCES dbo.recipe (recipe_code)
);

CREATE TABLE dbo.ingest_batch (
    batch_id            int IDENTITY(1,1) NOT NULL CONSTRAINT pk_ingest_batch PRIMARY KEY,
    equipment_id        varchar(20)   NOT NULL CONSTRAINT fk_ingest_batch_equipment REFERENCES dbo.equipment (equipment_id),
    source_name         nvarchar(260) NOT NULL,
    source_sha256       char(64)      NOT NULL,
    status              varchar(10)   NOT NULL CONSTRAINT ck_ingest_batch_status CHECK (status IN ('RUNNING', 'SUCCEEDED', 'FAILED')),
    started_at          datetime2(3)  NOT NULL CONSTRAINT df_ingest_batch_started DEFAULT sysutcdatetime(),
    finished_at         datetime2(3)  NULL,
    rows_read           int           NOT NULL CONSTRAINT df_ingest_batch_read DEFAULT 0,
    rows_loaded         int           NOT NULL CONSTRAINT df_ingest_batch_loaded DEFAULT 0,
    rows_rejected       int           NOT NULL CONSTRAINT df_ingest_batch_rejected DEFAULT 0,
    rows_already_loaded int           NOT NULL CONSTRAINT df_ingest_batch_already DEFAULT 0,
    modules_closed      int           NOT NULL CONSTRAINT df_ingest_batch_modules DEFAULT 0,
    error_message       nvarchar(max) NULL
);

-- K1: 성공한 배치만 유일. 실패한 배치는 같은 파일로 다시 시도할 수 있어야 한다.
CREATE UNIQUE INDEX ux_ingest_batch_source
    ON dbo.ingest_batch (equipment_id, source_sha256)
    WHERE status = 'SUCCEEDED';

CREATE TABLE dbo.data_issue (
    issue_id   bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_data_issue PRIMARY KEY,
    batch_id   int           NOT NULL CONSTRAINT fk_data_issue_batch REFERENCES dbo.ingest_batch (batch_id),
    line_no    int           NULL,
    code       varchar(4)    NOT NULL,
    level      varchar(7)    NOT NULL CONSTRAINT ck_data_issue_level CHECK (level IN ('INFO', 'WARNING', 'ERROR')),
    message    nvarchar(400) NOT NULL
);

CREATE INDEX ix_data_issue_batch ON dbo.data_issue (batch_id);

CREATE TABLE dbo.module (
    module_id         int IDENTITY(1,1) NOT NULL CONSTRAINT pk_module PRIMARY KEY,
    equipment_id      varchar(20)  NOT NULL CONSTRAINT fk_module_equipment REFERENCES dbo.equipment (equipment_id),
    recipe_code       varchar(30)  NOT NULL CONSTRAINT fk_module_recipe REFERENCES dbo.recipe (recipe_code),
    start_time        datetime2(3) NOT NULL,
    end_time          datetime2(3) NOT NULL,
    weld_count        int          NOT NULL,
    is_complete       bit          NOT NULL,
    verdict           varchar(10)  NOT NULL CONSTRAINT ck_module_verdict CHECK (verdict IN ('OK', 'WARNING', 'NG', 'HOLD')),
    primary_fault     varchar(20)  NULL CONSTRAINT ck_module_fault CHECK (primary_fault IN ('NO_OUTPUT', 'POWER_DROP', 'RECIPE_DEVIATION')),
    mean_power_ratio  float        NULL,
    closed_by_batch   int          NOT NULL CONSTRAINT fk_module_batch REFERENCES dbo.ingest_batch (batch_id),
    closed_at         datetime2(3) NOT NULL CONSTRAINT df_module_closed_at DEFAULT sysutcdatetime(),
    CONSTRAINT uq_module_equipment_start UNIQUE (equipment_id, start_time),
    CONSTRAINT ck_module_time CHECK (end_time >= start_time)
);

CREATE TABLE dbo.weld (
    weld_id        bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_weld PRIMARY KEY,
    equipment_id   varchar(20)  NOT NULL CONSTRAINT fk_weld_equipment REFERENCES dbo.equipment (equipment_id),
    working_time   datetime2(3) NOT NULL,
    page_no        int          NOT NULL,
    speed          int          NOT NULL,
    length_mm      decimal(6,1) NOT NULL,
    real_power     int          NOT NULL CONSTRAINT ck_weld_real_power CHECK (real_power >= 0),
    set_frequency  int          NOT NULL,
    set_duty       int          NOT NULL,
    set_power      int          NOT NULL,
    gate_on_time   int          NOT NULL,
    power_ratio    float        NULL,
    batch_id       int          NOT NULL CONSTRAINT fk_weld_batch REFERENCES dbo.ingest_batch (batch_id),
    module_id      int          NULL CONSTRAINT fk_weld_module REFERENCES dbo.module (module_id),
    CONSTRAINT uq_weld_equipment_time UNIQUE (equipment_id, working_time)
);

-- 아직 모듈에 묶이지 않은 용접(열린 모듈)을 도착 순서대로 빨리 찾기 위한 필터 인덱스
CREATE INDEX ix_weld_open ON dbo.weld (equipment_id, weld_id) WHERE module_id IS NULL;
CREATE INDEX ix_weld_module ON dbo.weld (module_id);

CREATE TABLE dbo.weld_fault (
    weld_id     bigint      NOT NULL CONSTRAINT fk_weld_fault_weld REFERENCES dbo.weld (weld_id),
    fault_code  varchar(20) NOT NULL CONSTRAINT ck_weld_fault_code CHECK (fault_code IN ('NO_OUTPUT', 'POWER_DROP', 'RECIPE_DEVIATION')),
    CONSTRAINT pk_weld_fault PRIMARY KEY (weld_id, fault_code)
);
