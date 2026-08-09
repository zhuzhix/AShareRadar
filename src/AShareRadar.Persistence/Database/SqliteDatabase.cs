using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Database;

public sealed class SqliteDatabase
{
    private readonly DatabaseOptions _options;

    public SqliteDatabase(DatabaseOptions options)
    {
        _options = options;
    }

    public string DatabasePath => ResolvePath(_options.SqlitePath);

    public SqliteConnection OpenConnection()
    {
        var path = DatabasePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    public void EnsureCreated()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS opportunities (
                id TEXT PRIMARY KEY,
                trading_date TEXT NOT NULL,
                symbol TEXT NOT NULL,
                name TEXT NOT NULL,
                first_seen_time TEXT NOT NULL,
                last_seen_time TEXT NOT NULL,
                status TEXT NOT NULL,
                hit_count INTEGER NOT NULL,
                current_score TEXT NOT NULL,
                best_score TEXT NOT NULL,
                manual_tag TEXT NULL,
                note TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS signal_events (
                id TEXT PRIMARY KEY,
                opportunity_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                event_time TEXT NOT NULL,
                event_type TEXT NOT NULL,
                symbol TEXT NOT NULL,
                name TEXT NOT NULL,
                strategy_code TEXT NOT NULL,
                strategy_name TEXT NOT NULL,
                score TEXT NOT NULL,
                price TEXT NULL,
                reason TEXT NOT NULL,
                risk TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS strategy_hits (
                event_id TEXT NOT NULL,
                row_index INTEGER NOT NULL,
                strategy_code TEXT NOT NULL,
                strategy_name TEXT NOT NULL,
                score TEXT NOT NULL,
                price TEXT NULL,
                reason TEXT NOT NULL,
                risk TEXT NULL,
                metrics_json TEXT NULL,
                tags_json TEXT NULL,
                passed_conditions_json TEXT NULL,
                failed_conditions_json TEXT NULL,
                stop_loss_price TEXT NULL,
                take_profit_price TEXT NULL,
                PRIMARY KEY (event_id, row_index)
            );

            CREATE TABLE IF NOT EXISTS strategy_versions (
                id TEXT PRIMARY KEY,
                strategy_code TEXT NOT NULL,
                strategy_name TEXT NOT NULL,
                version TEXT NOT NULL,
                status TEXT NOT NULL,
                rule_summary TEXT NOT NULL,
                parameter_json TEXT NOT NULL,
                data_requirement_json TEXT NOT NULL,
                definition_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                activated_at TEXT NULL,
                deactivated_at TEXT NULL,
                source TEXT NOT NULL,
                UNIQUE(strategy_code, version)
            );

            CREATE TABLE IF NOT EXISTS strategy_hit_versions (
                event_id TEXT NOT NULL,
                strategy_code TEXT NOT NULL,
                strategy_version_id TEXT NOT NULL,
                version TEXT NOT NULL,
                parameter_json TEXT NOT NULL,
                rule_summary TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (event_id, strategy_code)
            );

            CREATE TABLE IF NOT EXISTS signal_heat_contexts (
                event_id TEXT NOT NULL,
                row_index INTEGER NOT NULL,
                symbol TEXT NOT NULL,
                event_time TEXT NOT NULL,
                context_type TEXT NOT NULL,
                code TEXT NOT NULL,
                name TEXT NOT NULL,
                heat_rank INTEGER NOT NULL,
                stock_count INTEGER NOT NULL,
                rising_count INTEGER NOT NULL,
                average_change_percent TEXT NOT NULL,
                rising_ratio_percent TEXT NOT NULL,
                total_amount TEXT NOT NULL,
                heat_score TEXT NOT NULL,
                is_leader INTEGER NOT NULL,
                heat_snapshot_batch_id TEXT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (event_id, row_index)
            );

            CREATE TABLE IF NOT EXISTS signal_return_records (
                event_id TEXT NOT NULL,
                opportunity_id TEXT NOT NULL,
                event_time TEXT NOT NULL,
                signal_date TEXT NOT NULL,
                symbol TEXT NOT NULL,
                name TEXT NOT NULL,
                strategy_code TEXT NOT NULL,
                strategy_name TEXT NOT NULL,
                strategy_group TEXT NOT NULL,
                strategy_version_id TEXT NULL,
                strategy_version TEXT NULL,
                score TEXT NOT NULL,
                signal_price TEXT NULL,
                entry_price TEXT NOT NULL,
                horizon_code TEXT NOT NULL,
                horizon_name TEXT NOT NULL,
                trading_days INTEGER NOT NULL,
                horizon_group TEXT NOT NULL,
                target_date TEXT NULL,
                target_close TEXT NULL,
                return_percent TEXT NULL,
                max_return_percent TEXT NULL,
                min_return_percent TEXT NULL,
                status TEXT NOT NULL,
                calculated_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (event_id, strategy_code, horizon_code)
            );

            CREATE TABLE IF NOT EXISTS prediction_records (
                id TEXT PRIMARY KEY,
                signal_date TEXT NOT NULL,
                symbol TEXT NOT NULL,
                name TEXT NOT NULL,
                strategy_codes TEXT NOT NULL,
                strategy_names TEXT NOT NULL,
                signal_count INTEGER NOT NULL,
                strategy_hit_count INTEGER NOT NULL,
                score TEXT NOT NULL,
                best_score TEXT NOT NULL,
                prediction_direction TEXT NOT NULL,
                prediction_score TEXT NOT NULL,
                prediction_reason TEXT NOT NULL,
                risk_note TEXT NOT NULL,
                verify_date TEXT NULL,
                next_open_return TEXT NULL,
                next_close_return TEXT NULL,
                next_high_return TEXT NULL,
                next_low_return TEXT NULL,
                is_close_success INTEGER NULL,
                is_intraday_success INTEGER NULL,
                verify_status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                verified_at TEXT NULL,
                UNIQUE(signal_date, symbol)
            );

            CREATE TABLE IF NOT EXISTS market_sentiment_snapshots (
                id TEXT PRIMARY KEY,
                snapshot_time TEXT NOT NULL,
                provider_name TEXT NOT NULL,
                temperature_score TEXT NOT NULL,
                level TEXT NOT NULL,
                summary TEXT NOT NULL,
                data_quality TEXT NOT NULL,
                categories_json TEXT NOT NULL,
                metrics_json TEXT NOT NULL,
                warnings_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mapping_snapshot_batches (
                id TEXT PRIMARY KEY,
                mapping_type TEXT NOT NULL,
                snapshot_time TEXT NOT NULL,
                trade_date TEXT NOT NULL,
                source TEXT NOT NULL,
                item_count INTEGER NOT NULL,
                file_hash TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mapping_snapshot_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                batch_id TEXT NOT NULL,
                mapping_type TEXT NOT NULL,
                board_code TEXT NOT NULL,
                board_name TEXT NOT NULL,
                board_rank INTEGER NOT NULL,
                symbol TEXT NOT NULL,
                stock_name TEXT NULL,
                source TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS heat_snapshot_batches (
                id TEXT PRIMARY KEY,
                snapshot_time TEXT NOT NULL,
                trade_date TEXT NOT NULL,
                sector_mapping_batch_id TEXT NULL,
                concept_mapping_batch_id TEXT NULL,
                source TEXT NOT NULL,
                sector_count INTEGER NOT NULL,
                concept_count INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sector_heat_snapshots (
                batch_id TEXT NOT NULL,
                row_index INTEGER NOT NULL,
                snapshot_time TEXT NOT NULL,
                trade_date TEXT NOT NULL,
                mapping_batch_id TEXT NULL,
                sector_code TEXT NOT NULL,
                sector_name TEXT NOT NULL,
                heat_rank INTEGER NOT NULL,
                stock_count INTEGER NOT NULL,
                rising_count INTEGER NOT NULL,
                average_change_percent TEXT NOT NULL,
                rising_ratio_percent TEXT NOT NULL,
                total_amount TEXT NOT NULL,
                heat_score TEXT NOT NULL,
                leaders_json TEXT NOT NULL,
                leader_symbols_json TEXT NOT NULL,
                PRIMARY KEY (batch_id, row_index)
            );

            CREATE TABLE IF NOT EXISTS concept_heat_snapshots (
                batch_id TEXT NOT NULL,
                row_index INTEGER NOT NULL,
                snapshot_time TEXT NOT NULL,
                trade_date TEXT NOT NULL,
                mapping_batch_id TEXT NULL,
                concept_code TEXT NOT NULL,
                concept_name TEXT NOT NULL,
                heat_rank INTEGER NOT NULL,
                stock_count INTEGER NOT NULL,
                rising_count INTEGER NOT NULL,
                average_change_percent TEXT NOT NULL,
                rising_ratio_percent TEXT NOT NULL,
                total_amount TEXT NOT NULL,
                heat_score TEXT NOT NULL,
                leaders_json TEXT NOT NULL,
                leader_symbols_json TEXT NOT NULL,
                PRIMARY KEY (batch_id, row_index)
            );

            CREATE TABLE IF NOT EXISTS long_term_tracking_items (
                id TEXT PRIMARY KEY,
                symbol TEXT NOT NULL,
                name TEXT NOT NULL,
                strategy_code TEXT NOT NULL,
                strategy_name TEXT NOT NULL,
                first_hit_at TEXT NOT NULL,
                last_hit_at TEXT NOT NULL,
                hit_count INTEGER NOT NULL,
                latest_price TEXT NULL,
                latest_score TEXT NOT NULL,
                best_score TEXT NOT NULL,
                latest_reason TEXT NOT NULL,
                latest_risk TEXT NULL,
                status TEXT NOT NULL,
                manual_priority INTEGER NOT NULL DEFAULT 0,
                note TEXT NULL,
                tags TEXT NULL,
                latest_event_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(symbol, strategy_code)
            );

            CREATE TABLE IF NOT EXISTS background_jobs (
                id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                progress_percent INTEGER NOT NULL,
                current_step TEXT NOT NULL,
                created_at TEXT NOT NULL,
                started_at TEXT NULL,
                finished_at TEXT NULL,
                exit_code INTEGER NULL,
                error_message TEXT NULL,
                fix_suggestion TEXT NULL,
                payload_json TEXT NOT NULL,
                result_json TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS background_job_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                stream TEXT NOT NULL,
                message TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS auction_watch_pool (
                trading_date TEXT NOT NULL,
                reference_trade_date TEXT NOT NULL,
                symbol TEXT NOT NULL,
                name TEXT NOT NULL,
                source_rank INTEGER NOT NULL,
                source_score TEXT NOT NULL,
                source_strategies TEXT NOT NULL,
                source_hit_time TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (trading_date, symbol)
            );

            CREATE TABLE IF NOT EXISTS auction_ticks (
                trading_date TEXT NOT NULL,
                symbol TEXT NOT NULL,
                event_time TEXT NOT NULL,
                name TEXT NOT NULL,
                price TEXT NULL,
                pre_close TEXT NOT NULL,
                cum_volume TEXT NOT NULL,
                cum_amount TEXT NOT NULL,
                quotes_json TEXT NOT NULL,
                PRIMARY KEY (trading_date, symbol, event_time)
            );

            CREATE INDEX IF NOT EXISTS ix_opportunities_symbol ON opportunities(symbol);
            CREATE INDEX IF NOT EXISTS ix_signal_events_opportunity_id ON signal_events(opportunity_id);
            CREATE INDEX IF NOT EXISTS ix_signal_events_event_time ON signal_events(event_time);
            CREATE INDEX IF NOT EXISTS ix_strategy_versions_code_status ON strategy_versions(strategy_code, status);
            CREATE INDEX IF NOT EXISTS ix_strategy_versions_code_version ON strategy_versions(strategy_code, version);
            CREATE INDEX IF NOT EXISTS ix_strategy_hit_versions_event ON strategy_hit_versions(event_id);
            CREATE INDEX IF NOT EXISTS ix_strategy_hit_versions_strategy ON strategy_hit_versions(strategy_code, version);
            CREATE INDEX IF NOT EXISTS ix_signal_heat_contexts_event_id ON signal_heat_contexts(event_id);
            CREATE INDEX IF NOT EXISTS ix_signal_heat_contexts_symbol_time ON signal_heat_contexts(symbol, event_time);
            CREATE INDEX IF NOT EXISTS ix_signal_heat_contexts_type_name ON signal_heat_contexts(context_type, name, event_time);
            CREATE INDEX IF NOT EXISTS ix_signal_return_records_signal_date ON signal_return_records(signal_date);
            CREATE INDEX IF NOT EXISTS ix_signal_return_records_strategy ON signal_return_records(strategy_code, horizon_code, signal_date);
            CREATE INDEX IF NOT EXISTS ix_signal_return_records_group ON signal_return_records(strategy_group, horizon_group, signal_date);
            CREATE INDEX IF NOT EXISTS ix_signal_return_records_symbol ON signal_return_records(symbol, signal_date);
            CREATE INDEX IF NOT EXISTS ix_signal_return_records_version ON signal_return_records(strategy_code, strategy_version, horizon_code, signal_date);
            CREATE INDEX IF NOT EXISTS ix_prediction_records_date ON prediction_records(signal_date);
            CREATE INDEX IF NOT EXISTS ix_market_sentiment_snapshot_time ON market_sentiment_snapshots(snapshot_time);
            CREATE INDEX IF NOT EXISTS ix_mapping_snapshot_type_time ON mapping_snapshot_batches(mapping_type, snapshot_time);
            CREATE INDEX IF NOT EXISTS ix_mapping_snapshot_items_batch ON mapping_snapshot_items(batch_id);
            CREATE INDEX IF NOT EXISTS ix_mapping_snapshot_items_symbol ON mapping_snapshot_items(mapping_type, symbol);
            CREATE INDEX IF NOT EXISTS ix_heat_snapshot_batches_time ON heat_snapshot_batches(snapshot_time);
            CREATE INDEX IF NOT EXISTS ix_heat_snapshot_batches_trade_date ON heat_snapshot_batches(trade_date, snapshot_time);
            CREATE INDEX IF NOT EXISTS ix_sector_heat_snapshot_time ON sector_heat_snapshots(snapshot_time, heat_rank);
            CREATE INDEX IF NOT EXISTS ix_sector_heat_snapshot_name ON sector_heat_snapshots(sector_name, snapshot_time);
            CREATE INDEX IF NOT EXISTS ix_concept_heat_snapshot_time ON concept_heat_snapshots(snapshot_time, heat_rank);
            CREATE INDEX IF NOT EXISTS ix_concept_heat_snapshot_name ON concept_heat_snapshots(concept_name, snapshot_time);
            CREATE INDEX IF NOT EXISTS ix_long_term_tracking_last_hit_at ON long_term_tracking_items(last_hit_at);
            CREATE INDEX IF NOT EXISTS ix_long_term_tracking_strategy ON long_term_tracking_items(strategy_code, status);
            CREATE INDEX IF NOT EXISTS ix_long_term_tracking_symbol ON long_term_tracking_items(symbol);
            CREATE INDEX IF NOT EXISTS ix_background_jobs_type_created ON background_jobs(type, created_at);
            CREATE INDEX IF NOT EXISTS ix_background_jobs_status ON background_jobs(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_background_job_logs_job ON background_job_logs(job_id, id);
            CREATE INDEX IF NOT EXISTS ix_auction_watch_pool_date_rank ON auction_watch_pool(trading_date, source_rank);
            CREATE INDEX IF NOT EXISTS ix_auction_ticks_date_symbol_time ON auction_ticks(trading_date, symbol, event_time);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "strategy_hits", "metrics_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "tags_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "passed_conditions_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "failed_conditions_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "stop_loss_price", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "take_profit_price", "TEXT NULL");
        EnsureColumn(connection, "signal_return_records", "strategy_version_id", "TEXT NULL");
        EnsureColumn(connection, "signal_return_records", "strategy_version", "TEXT NULL");
        DropDeprecatedStockPoolTables(connection);
        DropDeprecatedStrategyTrainingTables(connection);
        DropDeprecatedLowSparkTables(connection);
    }

    private static void DropDeprecatedStockPoolTables(SqliteConnection connection)
    {
        using var drop = connection.CreateCommand();
        drop.CommandText = """
            DROP TABLE IF EXISTS stock_pool_review_sources;
            DROP TABLE IF EXISTS stock_pool_items;
            DROP TABLE IF EXISTS stock_pools;
            """;
        drop.ExecuteNonQuery();
    }

    private static void DropDeprecatedStrategyTrainingTables(SqliteConnection connection)
    {
        using var drop = connection.CreateCommand();
        drop.CommandText = """
            DROP TABLE IF EXISTS strategy_training_results;
            DROP TABLE IF EXISTS strategy_training_runs;
            DROP TABLE IF EXISTS strategy_training_samples;
            DROP TABLE IF EXISTS strategy_parameter_profiles;
            """;
        drop.ExecuteNonQuery();
    }

    private static void DropDeprecatedLowSparkTables(SqliteConnection connection)
    {
        using var drop = connection.CreateCommand();
        drop.CommandText = """
            DROP TABLE IF EXISTS qlib_signal_seeds;
            """;
        drop.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }
}
