-- Per-day, per-skill, per-load-method usage counters.
-- Written by DailyStatsActor when a RecordSkillLoaded message lands.
-- Consumed by GET /api/stats/skills?days=N for the wizard's usage breakdown.

CREATE TABLE IF NOT EXISTS daily_skill_usage (
    date_key    TEXT NOT NULL,
    skill_name  TEXT NOT NULL,
    load_method TEXT NOT NULL,
    count       INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (date_key, skill_name, load_method)
);
