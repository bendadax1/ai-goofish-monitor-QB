"""便携 PostgreSQL 的静态 schema 清单。

v1 清单是已发行结构的快照，不从未来可能扩展的 ORM 表集合推导。
迁移、备份、恢复和测试必须按版本选择清单，不能把 v2 表误认为 v1。
"""

from __future__ import annotations

V1_TABLE_COLUMNS: dict[str, tuple[str, ...]] = {
    "ai_criteria": ("id", "owner_id", "name", "content", "is_default", "created_at", "updated_at"),
    "audit_logs": ("id", "user_id", "action", "resource_type", "resource_id", "details", "ip_address", "created_at"),
    "bayes_profiles": ("id", "owner_id", "version", "display_name", "recommendation_fusion", "bayes_feature_rules", "is_default", "created_at", "updated_at"),
    "bayes_samples": ("id", "owner_id", "profile_id", "profile_version", "name", "vector", "label", "source", "item_id", "note", "created_at"),
    "group_permissions": ("id", "group_id", "category", "enabled", "updated_at"),
    "monitoring_results": ("id", "owner_id", "task_id", "item_id", "product_info", "seller_info", "ai_analysis", "ml_precalc", "recommendation_score", "is_recommended", "crawled_at"),
    "prompt_templates": ("id", "owner_id", "name", "content", "is_default", "created_at", "updated_at"),
    "sessions": ("id", "user_id", "token_hash", "user_agent", "ip_address", "expires_at", "created_at"),
    "tasks": ("id", "owner_id", "task_name", "order", "enabled", "keyword", "description", "max_pages", "personal_only", "min_price", "max_price", "cron", "filters", "bayes_profile", "ai_criteria_id", "bound_account_id", "created_at", "updated_at"),
    "user_api_configs": ("id", "user_id", "provider", "name", "api_key_encrypted", "api_base_url", "model", "extra_config", "is_default", "created_at", "updated_at"),
    "user_feedbacks": ("id", "user_id", "result_id", "feedback_type", "feature_vector", "created_at"),
    "user_group_members": ("id", "user_id", "group_id", "joined_at"),
    "user_groups": ("id", "code", "name", "description", "is_system", "created_by", "created_at", "updated_at"),
    "user_notification_configs": ("id", "user_id", "channel_type", "name", "config_encrypted", "is_enabled", "notify_on_complete", "notify_on_recommend", "created_at", "updated_at"),
    "user_platform_accounts": ("id", "user_id", "platform", "display_name", "cookies_encrypted", "risk_control_count", "risk_control_history", "last_used_at", "is_active", "created_at", "updated_at"),
    "users": ("id", "username", "password_hash", "email", "role", "is_active", "last_login_at", "created_at", "updated_at"),
}

V1_INDEXES: frozenset[str] = frozenset({
    "idx_api_config_user", "idx_audit_action", "idx_audit_created", "idx_audit_user", "idx_bayes_owner",
    "idx_criteria_owner", "idx_feedback_result", "idx_feedback_user",
    "idx_group_permission_group", "idx_notify_config_user", "idx_platform_account_user",
    "idx_prompt_owner", "idx_result_owner", "idx_result_task", "idx_sample_label",
    "idx_sample_owner", "idx_sample_profile", "idx_task_owner", "idx_user_group_member_group",
    "idx_user_group_member_user", "idx_user_group_system", "ix_public_monitoring_results_item_id",
    "ix_public_sessions_token_hash", "ix_public_user_groups_code", "ix_public_users_username",
})

V1_TABLES = frozenset((*V1_TABLE_COLUMNS, "app_schema_version"))

V2_BUSINESS_TABLES = frozenset({
    "result_hidden_items", "result_blacklist_rules", "result_view_preferences",
    "price_observations", "criteria_generation_jobs",
})
V2_TABLES = V1_TABLES | V2_BUSINESS_TABLES | {"app_schema_migrations"}

# PostgreSQL 17.11、固定 search_path 下的 pg_catalog 结构指纹。
# 覆盖表/列类型/默认值/非空/约束/索引/函数/独立类型。由两套全新隔离
# 集群分别计算并核对一致；升级执行前后必须精确匹配。
V1_FINGERPRINT = "6887006d68420a65738f2c40f223af242e5a8d0a5f95625c3444902a49746f91"
V2_FINGERPRINT = "3ef0ea1f60807abeb5e8e1ad7efa85c731a9411fb81d533c13f24b2a91ab840f"


def expected_tables(version: int) -> frozenset[str]:
    if version == 1:
        return V1_TABLES
    if version == 2:
        return V2_TABLES
    raise ValueError("unsupported portable schema version")
