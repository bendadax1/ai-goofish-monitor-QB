"""002_upstream_features 的显式 PostgreSQL DDL 输入。

这里只定义空结构，不执行迁移。执行器必须在停写、备份、实例身份和
严格 v1 指纹验证后，统一事务中运行这些固定语句。
"""

from __future__ import annotations

import hashlib

MIGRATION_ID = "002_upstream_features"
MIGRATION_CHECKSUM = "5342148c3659ff5bf984c1aac0b5879033296be50328b8b233b65bc6d864770c"

CREATE_STATEMENTS: tuple[str, ...] = (
    """CREATE TABLE public.result_hidden_items (
        owner_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
        item_id varchar(128) NOT NULL CHECK (length(btrim(item_id)) > 0),
        created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
        PRIMARY KEY (owner_id, item_id)
    )""",
    """CREATE TABLE public.result_blacklist_rules (
        id uuid PRIMARY KEY,
        owner_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
        scope varchar(8) NOT NULL CHECK (scope = 'task' OR scope = 'global'),
        task_ref uuid,
        task_name_snapshot varchar(255),
        kind varchar(32) NOT NULL,
        pattern varchar(512) NOT NULL CHECK (length(pattern) > 0),
        enabled boolean NOT NULL DEFAULT true,
        revision integer NOT NULL DEFAULT 1 CHECK (revision > 0),
        created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
        updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
        CONSTRAINT ck_blacklist_scope_task CHECK (
            (scope = 'task' AND task_ref IS NOT NULL) OR
            (scope = 'global' AND task_ref IS NULL)
        )
    )""",
    """CREATE INDEX idx_blacklist_owner_scope_task_enabled
        ON public.result_blacklist_rules (owner_id, scope, task_ref, enabled)""",
    """CREATE TABLE public.result_view_preferences (
        owner_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
        page_key varchar(32) NOT NULL CHECK (page_key = 'results'),
        task_ref uuid,
        filters jsonb NOT NULL DEFAULT '{}'::jsonb,
        sort_by varchar(32) NOT NULL DEFAULT 'crawled_at',
        sort_order varchar(4) NOT NULL DEFAULT 'desc' CHECK (sort_order = 'asc' OR sort_order = 'desc'),
        revision integer NOT NULL DEFAULT 1 CHECK (revision > 0),
        updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
        PRIMARY KEY (owner_id, page_key)
    )""",
    """CREATE TABLE public.price_observations (
        id uuid PRIMARY KEY,
        owner_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
        task_ref uuid NOT NULL,
        task_name_snapshot varchar(255) NOT NULL,
        item_id varchar(128) NOT NULL CHECK (length(btrim(item_id)) > 0),
        run_id uuid NOT NULL,
        observed_at timestamptz NOT NULL,
        currency varchar(8) NOT NULL,
        raw_price varchar(256) NOT NULL,
        amount numeric(14,2) CHECK (amount >= 0),
        source varchar(32) NOT NULL,
        CONSTRAINT uq_price_owner_task_item_run UNIQUE (owner_id, task_ref, item_id, run_id)
    )""",
    """CREATE INDEX idx_price_owner_task_observed
        ON public.price_observations (owner_id, task_ref, observed_at DESC)""",
    """CREATE INDEX idx_price_owner_item_observed
        ON public.price_observations (owner_id, item_id, observed_at DESC)""",
    """CREATE TABLE public.criteria_generation_jobs (
        id uuid PRIMARY KEY,
        owner_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
        task_ref uuid NOT NULL,
        task_name_snapshot varchar(255) NOT NULL,
        input_digest char(64) NOT NULL CHECK (input_digest ~ '^[0-9a-f]{64}$'),
        idempotency_key varchar(128) NOT NULL CHECK (length(idempotency_key) > 0),
        attempt integer NOT NULL DEFAULT 1 CHECK (attempt > 0),
        status varchar(16) NOT NULL CHECK (status = 'queued' OR status = 'running' OR
            status = 'succeeded' OR status = 'failed' OR status = 'interrupted'),
        stage varchar(32) NOT NULL,
        error_code varchar(64),
        error_summary varchar(255),
        created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
        started_at timestamptz,
        finished_at timestamptz,
        CONSTRAINT uq_job_owner_idempotency UNIQUE (owner_id, idempotency_key),
        CONSTRAINT uq_job_owner_task_digest_attempt UNIQUE (owner_id, task_ref, input_digest, attempt)
    )""",
    """CREATE UNIQUE INDEX uq_job_running_owner_task
        ON public.criteria_generation_jobs (owner_id, task_ref)
        WHERE status = 'queued' OR status = 'running'""",
    """CREATE TABLE public.app_schema_migrations (
        migration_id varchar(64) PRIMARY KEY,
        checksum char(64) NOT NULL CHECK (checksum ~ '^[0-9a-f]{64}$'),
        applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
    )""",
)

V2_INDEXES = frozenset({
    "idx_blacklist_owner_scope_task_enabled", "idx_price_owner_task_observed",
    "idx_price_owner_item_observed", "uq_job_running_owner_task",
})


def ddl_checksum() -> str:
    """校验迁移文本是否与审核时冻结的输入完全相同。"""

    return hashlib.sha256("\n-- next statement --\n".join(CREATE_STATEMENTS).encode("utf-8")).hexdigest()
