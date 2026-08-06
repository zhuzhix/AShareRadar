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

            CREATE TABLE IF NOT EXISTS qlib_signal_seeds (
                id TEXT PRIMARY KEY,
                signal_date TEXT NOT NULL,
                code TEXT NOT NULL,
                symbol TEXT NOT NULL,
                exchange TEXT NOT NULL,
                name TEXT NOT NULL,
                pred_score TEXT NOT NULL,
                rank_total INTEGER NOT NULL,
                model_rank INTEGER NOT NULL,
                model_score_100 TEXT NOT NULL,
                target_weight TEXT NOT NULL,
                action TEXT NOT NULL,
                confidence TEXT NOT NULL,
                strategy_code TEXT NOT NULL,
                strategy_name TEXT NOT NULL,
                source_experiment_id TEXT NOT NULL,
                reason TEXT NOT NULL,
                risk TEXT NULL,
                imported_at TEXT NOT NULL,
                UNIQUE(signal_date, strategy_code, symbol)
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

            CREATE TABLE IF NOT EXISTS strategy_training_samples (
                id TEXT PRIMARY KEY,
                signal_date TEXT NOT NULL,
                symbol TEXT NOT NULL,
                name TEXT NOT NULL,
                strategy_code TEXT NOT NULL,
                strategy_name TEXT NOT NULL,
                score TEXT NOT NULL,
                price TEXT NULL,
                amount_yi TEXT NULL,
                change_percent TEXT NULL,
                volume_ratio TEXT NULL,
                relative_strength_percent TEXT NULL,
                sector_heat_score TEXT NULL,
                concept_heat_score TEXT NULL,
                sentiment_temperature TEXT NULL,
                next_open_return TEXT NULL,
                next_high_return TEXT NULL,
                next_close_return TEXT NULL,
                is_success INTEGER NOT NULL,
                reason TEXT NOT NULL,
                evaluation_days INTEGER NOT NULL DEFAULT 1,
                metrics_json TEXT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(signal_date, symbol, strategy_code)
            );

            CREATE TABLE IF NOT EXISTS strategy_training_runs (
                id TEXT PRIMARY KEY,
                start_date TEXT NOT NULL,
                end_date TEXT NOT NULL,
                strategy_code TEXT NULL,
                source_signal_count INTEGER NOT NULL,
                sample_count INTEGER NOT NULL,
                result_count INTEGER NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS strategy_training_results (
                run_id TEXT NOT NULL,
                rank INTEGER NOT NULL,
                min_score TEXT NOT NULL,
                min_amount_yi TEXT NOT NULL,
                min_relative_strength_percent TEXT NOT NULL,
                min_heat_score TEXT NOT NULL,
                max_output_per_day INTEGER NOT NULL,
                hit_count INTEGER NOT NULL,
                success_count INTEGER NOT NULL,
                success_rate TEXT NULL,
                average_next_open_return TEXT NULL,
                average_next_high_return TEXT NULL,
                average_next_close_return TEXT NULL,
                worst_next_close_return TEXT NULL,
                summary TEXT NOT NULL,
                PRIMARY KEY (run_id, rank)
            );

            CREATE TABLE IF NOT EXISTS strategy_parameter_profiles (
                id TEXT PRIMARY KEY,
                strategy_code TEXT NOT NULL,
                profile_name TEXT NOT NULL,
                source_training_run_id TEXT NULL,
                parameters_json TEXT NOT NULL,
                sample_count INTEGER NOT NULL,
                success_rate TEXT NULL,
                average_next_high_return TEXT NULL,
                average_next_close_return TEXT NULL,
                is_active INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                activated_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_opportunities_symbol ON opportunities(symbol);
            CREATE INDEX IF NOT EXISTS ix_signal_events_opportunity_id ON signal_events(opportunity_id);
            CREATE INDEX IF NOT EXISTS ix_signal_events_event_time ON signal_events(event_time);
            CREATE INDEX IF NOT EXISTS ix_prediction_records_date ON prediction_records(signal_date);
            CREATE INDEX IF NOT EXISTS ix_market_sentiment_snapshot_time ON market_sentiment_snapshots(snapshot_time);
            CREATE INDEX IF NOT EXISTS ix_qlib_signal_seeds_date ON qlib_signal_seeds(signal_date, strategy_code);
            CREATE INDEX IF NOT EXISTS ix_qlib_signal_seeds_symbol ON qlib_signal_seeds(symbol);
            CREATE INDEX IF NOT EXISTS ix_strategy_training_samples_date ON strategy_training_samples(signal_date);
            CREATE INDEX IF NOT EXISTS ix_strategy_training_samples_strategy ON strategy_training_samples(strategy_code, signal_date);
            CREATE INDEX IF NOT EXISTS ix_strategy_training_runs_created_at ON strategy_training_runs(created_at);
            CREATE INDEX IF NOT EXISTS ix_strategy_parameter_profiles_strategy ON strategy_parameter_profiles(strategy_code, is_active);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "strategy_hits", "metrics_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "tags_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "passed_conditions_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "failed_conditions_json", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "stop_loss_price", "TEXT NULL");
        EnsureColumn(connection, "strategy_hits", "take_profit_price", "TEXT NULL");
        EnsureColumn(connection, "strategy_training_samples", "evaluation_days", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "strategy_training_samples", "metrics_json", "TEXT NULL");
        DropDeprecatedStockPoolTables(connection);
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
