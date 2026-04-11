using System.Collections.Generic;

namespace InfiniteDrive.Data
{
    /// <summary>
    /// v3.3 database schema definitions.
    /// This is a breaking change from v20 with full database reset required.
    /// Key changes: MediaId system, ItemStatus lifecycle, Sources model, Saved/Blocked states.
    /// </summary>
    public static class Schema
    {
        public const int CurrentSchemaVersion = 26;

        /// <summary>
        /// All v3.3 database tables with their CREATE SQL statements.
        /// Tables must be created in dependency order (no foreign key violations).
        /// </summary>
        public static readonly IReadOnlyList<TableDefinition> Tables = new List<TableDefinition>
        {
            // Core entity tables (no dependencies)
            new TableDefinition("schema_version", @"
CREATE TABLE schema_version (
    version     INTEGER PRIMARY KEY,
    description TEXT NOT NULL,
    applied_at  TEXT NOT NULL DEFAULT (datetime('now'))
);"),

            new TableDefinition("sources", @"
CREATE TABLE sources (
    id                TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    name              TEXT NOT NULL,
    url               TEXT,
    type              TEXT NOT NULL CHECK (type IN ('builtin','aio','trakt','mdblist')),
    enabled           INTEGER NOT NULL DEFAULT 1,
    show_as_collection INTEGER NOT NULL DEFAULT 0,
    max_items         INTEGER NOT NULL DEFAULT 100,
    sync_interval_hours INTEGER NOT NULL DEFAULT 6,
    last_synced_at   TEXT,
    emby_collection_id TEXT,
    collection_name    TEXT,
    created_at        TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at        TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_sources_enabled ON sources(enabled);
CREATE INDEX idx_sources_type ON sources(type);"),

            new TableDefinition("media_items", @"
CREATE TABLE media_items (
    id                  TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    primary_id_type      TEXT NOT NULL,
    primary_id           TEXT NOT NULL,
    media_type          TEXT NOT NULL CHECK (media_type IN ('movie','series')),
    title               TEXT NOT NULL,
    year                INTEGER,

    -- Lifecycle state
    status              TEXT NOT NULL CHECK (status IN ('known','resolved','hydrated','created','indexed','active','failed','deleted')),
    failure_reason       TEXT CHECK (failure_reason IN ('none','no_streams_found','metadata_fetch_failed','file_write_error','emby_index_timeout','digital_release_gate','blocked')),

    -- Saved state (denormalized flag — per-user saves live in user_item_saves)
    saved               INTEGER NOT NULL DEFAULT 0,
    saved_at            TEXT,

    -- Blocked state (boolean, not status value)
    blocked             INTEGER NOT NULL DEFAULT 0,
    blocked_at          TEXT,

    -- Timestamps
    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
    grace_started_at    TEXT,

    -- Superseded / conflict handling
    superseded          INTEGER NOT NULL DEFAULT 0,
    superseded_conflict INTEGER NOT NULL DEFAULT 0,
    superseded_at       TEXT,

    -- Emby integration
    emby_item_id        TEXT,
    emby_indexed_at     TEXT,
    strm_path           TEXT,
    nfo_path            TEXT,
    watch_progress_pct   INTEGER NOT NULL DEFAULT 0,
    favorited           INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX idx_media_items_primary ON media_items(primary_id_type, primary_id);
CREATE INDEX idx_media_items_status ON media_items(status);
CREATE INDEX idx_media_items_saved ON media_items(saved);
CREATE INDEX idx_media_items_blocked ON media_items(blocked);
CREATE INDEX idx_media_items_emby_id ON media_items(emby_item_id) WHERE emby_item_id IS NOT NULL;"),

            new TableDefinition("media_item_ids", @"
CREATE TABLE media_item_ids (
    id          TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id TEXT NOT NULL,
    id_type     TEXT NOT NULL CHECK (id_type IN ('tmdb','imdb','tvdb','anilist','anidb','kitsu')),
    id_value    TEXT NOT NULL,
    is_primary  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE
);
CREATE INDEX idx_media_item_ids_item ON media_item_ids(media_item_id);
CREATE INDEX idx_media_item_ids_type_value ON media_item_ids(id_type, id_value);
CREATE UNIQUE INDEX idx_media_item_ids_unique ON media_item_ids(media_item_id, id_type, id_value);"),

            new TableDefinition("source_memberships", @"
CREATE TABLE source_memberships (
    id           TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    source_id    TEXT NOT NULL,
    media_item_id TEXT NOT NULL,
    created_at   TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (source_id, media_item_id)
);
CREATE INDEX idx_source_memberships_source ON source_memberships(source_id);
CREATE INDEX idx_source_memberships_item ON source_memberships(media_item_id);"),

            new TableDefinition("collections", @"
CREATE TABLE collections (
    id                TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    name              TEXT NOT NULL UNIQUE,
    emby_collection_id TEXT,
    source_id         TEXT,
    enabled           INTEGER NOT NULL DEFAULT 1,
    show_as_collection INTEGER NOT NULL DEFAULT 0,
    created_at        TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at        TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE SET NULL
);
CREATE INDEX idx_collections_source ON collections(source_id);"),

            new TableDefinition("stream_resolution_log", @"
CREATE TABLE stream_resolution_log (
    id                TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    primary_id         TEXT NOT NULL,
    primary_id_type    TEXT NOT NULL,
    media_type         TEXT NOT NULL,
    season            INTEGER,
    episode           INTEGER,
    resolution_tier    TEXT NOT NULL,
    quality_tier      TEXT,
    stream_url        TEXT NOT NULL,
    file_name         TEXT,
    file_size         INTEGER,
    file_bitrate_kbps INTEGER,
    rd_cached         INTEGER NOT NULL DEFAULT 0,
    fallback_used     TEXT,
    resolved_at       TEXT NOT NULL DEFAULT (datetime('now')),
    error_message     TEXT
);
CREATE INDEX idx_stream_log_primary ON stream_resolution_log(primary_id, primary_id_type, media_type, season, episode);
CREATE INDEX idx_stream_log_time ON stream_resolution_log(resolved_at DESC);
CREATE INDEX idx_stream_log_tier ON stream_resolution_log(resolution_tier);"),

            new TableDefinition("item_pipeline_log", @"
CREATE TABLE item_pipeline_log (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    primary_id       TEXT NOT NULL,
    primary_id_type  TEXT NOT NULL,
    media_type       TEXT NOT NULL,
    trigger_type     TEXT NOT NULL CHECK (trigger_type IN ('sync','play','watch_episode','user_save','user_block','user_remove','grace_expiry','your_files','admin','retry')),
    from_status      TEXT,
    to_status        TEXT,
    result           TEXT NOT NULL CHECK (result IN ('success','failed','skipped')),
    error_message    TEXT,
    logged_at        TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_pipeline_log_primary ON item_pipeline_log(primary_id, primary_id_type, media_type);
CREATE INDEX idx_pipeline_log_time ON item_pipeline_log(logged_at DESC);
CREATE INDEX idx_pipeline_log_trigger ON item_pipeline_log(trigger_type);"),

            new TableDefinition("home_section_tracking", @"
CREATE TABLE home_section_tracking (
    id             TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    user_id        TEXT NOT NULL,
    rail_type       TEXT NOT NULL CHECK (rail_type IN ('saved','trending_movies','trending_series','new_this_week','admin_chosen')),
    emby_section_id TEXT,
    section_marker  TEXT NOT NULL,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (user_id, rail_type)
);
CREATE INDEX idx_home_section_user ON home_section_tracking(user_id);"),

            // Per-user saves (Sprint 207)
            new TableDefinition("user_item_saves", @"
CREATE TABLE user_item_saves (
    id            TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    user_id       TEXT NOT NULL,
    media_item_id TEXT NOT NULL,
    save_reason   TEXT CHECK (save_reason IN ('explicit','watched_episode','admin_override')),
    saved_season  INTEGER,
    saved_at      TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (user_id, media_item_id)
);
CREATE INDEX idx_user_saves_user ON user_item_saves(user_id);
CREATE INDEX idx_user_saves_item ON user_item_saves(media_item_id);"),

            // Stream cache for signed URLs (Sprint 112B)
            new TableDefinition("stream_cache", @"
CREATE TABLE stream_cache (
    media_id      TEXT    PRIMARY KEY NOT NULL,
    url           TEXT    NOT NULL,
    url_secondary TEXT,
    created_at    TEXT    NOT NULL DEFAULT (datetime('now')),
    expires_at    TEXT    NOT NULL
);
CREATE INDEX idx_cache_expires ON stream_cache(expires_at);"),

            // Versioned playback tables (Sprint 122)
            new TableDefinition("version_slots", @"
CREATE TABLE version_slots (
    slot_key        TEXT PRIMARY KEY,
    label           TEXT NOT NULL,
    resolution      TEXT NOT NULL,
    video_codecs    TEXT NOT NULL DEFAULT 'any',
    hdr_classes     TEXT NOT NULL DEFAULT '',
    audio_preferences TEXT NOT NULL,
    enabled         INTEGER NOT NULL DEFAULT 0,
    is_default      INTEGER NOT NULL DEFAULT 0,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);"),

            new TableDefinition("candidates", @"
CREATE TABLE candidates (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    rank            INTEGER NOT NULL,
    service         TEXT,
    stream_type     TEXT NOT NULL DEFAULT 'debrid',
    resolution      TEXT,
    video_codec     TEXT,
    hdr_class       TEXT,
    audio_codec     TEXT,
    audio_channels  TEXT,
    file_name       TEXT,
    file_size       INTEGER,
    bitrate_kbps    INTEGER,
    languages       TEXT,
    source_type     TEXT,
    is_cached       INTEGER NOT NULL DEFAULT 0,
    fingerprint     TEXT NOT NULL,
    binge_group     TEXT,
    info_hash       TEXT,
    file_idx        INTEGER,
    confidence_score REAL NOT NULL DEFAULT 0.0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at      TEXT NOT NULL,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE
);
CREATE INDEX idx_candidates_item_slot ON candidates(media_item_id, slot_key, rank);
CREATE INDEX idx_candidates_fingerprint ON candidates(fingerprint);
CREATE INDEX idx_candidates_expires ON candidates(expires_at);
CREATE UNIQUE INDEX idx_candidates_unique ON candidates(media_item_id, slot_key, fingerprint);"),

            new TableDefinition("version_snapshots", @"
CREATE TABLE version_snapshots (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    candidate_id    TEXT NOT NULL,
    snapshot_at     TEXT NOT NULL DEFAULT (datetime('now')),
    playback_url    TEXT,
    playback_url_cached_at TEXT,
    playback_url_expires_at TEXT,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (media_item_id, slot_key)
);
CREATE INDEX idx_snapshots_item ON version_snapshots(media_item_id);"),

            new TableDefinition("materialized_versions", @"
CREATE TABLE materialized_versions (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    strm_path       TEXT NOT NULL,
    nfo_path        TEXT NOT NULL,
    strm_url_hash   TEXT NOT NULL,
    is_base         INTEGER NOT NULL DEFAULT 0,
    materialized_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (media_item_id, slot_key)
);
CREATE INDEX idx_materialized_item ON materialized_versions(media_item_id);
CREATE INDEX idx_materialized_slot ON materialized_versions(slot_key);
CREATE INDEX idx_materialized_base ON materialized_versions(is_base) WHERE is_base = 1;")
        };
    }

    /// <summary>
    /// Represents a database table with its name and creation SQL.
    /// </summary>
    public sealed class TableDefinition
    {
        /// <summary>
        /// Table name (used for logging and error messages).
        /// </summary>
        public string TableName { get; }

        /// <summary>
        /// SQL statement to create this table including any indexes.
        /// May contain multiple CREATE statements (table + indexes).
        /// </summary>
        public string CreateSql { get; }

        /// <summary>
        /// Creates a new table definition.
        /// </summary>
        /// <param name="tableName">Name of the table.</param>
        /// <param name="createSql">Full CREATE SQL including indexes.</param>
        public TableDefinition(string tableName, string createSql)
        {
            TableName = tableName;
            CreateSql = createSql;
        }
    }
}
