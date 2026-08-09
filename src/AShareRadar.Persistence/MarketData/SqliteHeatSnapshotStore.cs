using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AShareRadar.Application.MarketData;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.MarketData;

public sealed class SqliteHeatSnapshotStore : IHeatSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteHeatSnapshotStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public MappingSnapshotBatch SaveMappingSnapshot(
        string mappingType,
        DateTimeOffset snapshotTime,
        string source,
        IReadOnlyList<MappingSnapshotItem> items)
    {
        var normalizedType = NormalizeMappingType(mappingType);
        var createdAt = DateTimeOffset.Now;
        var tradeDate = DateOnly.FromDateTime(snapshotTime.LocalDateTime);
        var hash = ComputeMappingHash(normalizedType, items);
        var batch = new MappingSnapshotBatch(
            Guid.NewGuid().ToString("N"),
            normalizedType,
            snapshotTime,
            tradeDate,
            string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim(),
            items.Count,
            hash,
            createdAt);

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mapping_snapshot_batches(
                        id, mapping_type, snapshot_time, trade_date, source, item_count, file_hash, created_at)
                    VALUES(
                        $id, $mapping_type, $snapshot_time, $trade_date, $source, $item_count, $file_hash, $created_at);
                    """;
                Add(command, "$id", batch.Id);
                Add(command, "$mapping_type", batch.MappingType);
                Add(command, "$snapshot_time", FormatDateTime(batch.SnapshotTime));
                Add(command, "$trade_date", FormatDate(batch.TradeDate));
                Add(command, "$source", batch.Source);
                Add(command, "$item_count", batch.ItemCount);
                Add(command, "$file_hash", batch.FileHash);
                Add(command, "$created_at", FormatDateTime(batch.CreatedAt));
                command.ExecuteNonQuery();
            }

            var rowIndex = 0;
            foreach (var item in items)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mapping_snapshot_items(
                        batch_id, mapping_type, board_code, board_name, board_rank, symbol, stock_name, source)
                    VALUES(
                        $batch_id, $mapping_type, $board_code, $board_name, $board_rank, $symbol, $stock_name, $source);
                    """;
                Add(command, "$batch_id", batch.Id);
                Add(command, "$mapping_type", batch.MappingType);
                Add(command, "$board_code", item.BoardCode);
                Add(command, "$board_name", item.BoardName);
                Add(command, "$board_rank", item.BoardRank <= 0 ? ++rowIndex : item.BoardRank);
                Add(command, "$symbol", item.Symbol);
                Add(command, "$stock_name", (object?)item.StockName ?? DBNull.Value);
                Add(command, "$source", item.Source);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return batch;
    }

    public MappingSnapshotBatch? GetLatestMappingSnapshot(string mappingType)
    {
        var normalizedType = NormalizeMappingType(mappingType);
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, mapping_type, snapshot_time, trade_date, source, item_count, file_hash, created_at
                FROM mapping_snapshot_batches
                WHERE mapping_type = $mapping_type
                ORDER BY snapshot_time DESC, created_at DESC
                LIMIT 1;
                """;
            Add(command, "$mapping_type", normalizedType);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMappingBatch(reader) : null;
        }
    }

    public HeatSnapshotSaveResult SaveHeatSnapshot(
        DateOnly tradeDate,
        SectorHeatSnapshot sectorSnapshot,
        ConceptHeatSnapshot conceptSnapshot,
        TimeSpan minimumInterval)
    {
        var snapshotTime = sectorSnapshot.SnapshotTime;
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            var latest = GetLatestHeatBatch(connection);
            if (latest is not null && snapshotTime - latest.SnapshotTime < minimumInterval)
            {
                return new HeatSnapshotSaveResult(false, latest.Id, latest.SnapshotTime, "minimum-interval");
            }

            var sectorMappingBatch = GetLatestMappingSnapshotCore(connection, "sector");
            var conceptMappingBatch = GetLatestMappingSnapshotCore(connection, "concept");
            var batchId = Guid.NewGuid().ToString("N");
            var createdAt = DateTimeOffset.Now;
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO heat_snapshot_batches(
                        id, snapshot_time, trade_date, sector_mapping_batch_id, concept_mapping_batch_id,
                        source, sector_count, concept_count, created_at)
                    VALUES(
                        $id, $snapshot_time, $trade_date, $sector_mapping_batch_id, $concept_mapping_batch_id,
                        $source, $sector_count, $concept_count, $created_at);
                    """;
                Add(command, "$id", batchId);
                Add(command, "$snapshot_time", FormatDateTime(snapshotTime));
                Add(command, "$trade_date", FormatDate(tradeDate));
                Add(command, "$sector_mapping_batch_id", (object?)sectorMappingBatch?.Id ?? DBNull.Value);
                Add(command, "$concept_mapping_batch_id", (object?)conceptMappingBatch?.Id ?? DBNull.Value);
                Add(command, "$source", "realtime-scan");
                Add(command, "$sector_count", sectorSnapshot.SectorsByCode.Count);
                Add(command, "$concept_count", conceptSnapshot.ConceptsByCode.Count);
                Add(command, "$created_at", FormatDateTime(createdAt));
                command.ExecuteNonQuery();
            }

            InsertSectorHeatRows(connection, transaction, batchId, tradeDate, sectorMappingBatch?.Id, sectorSnapshot);
            InsertConceptHeatRows(connection, transaction, batchId, tradeDate, conceptMappingBatch?.Id, conceptSnapshot);
            transaction.Commit();

            return new HeatSnapshotSaveResult(true, batchId, snapshotTime, "saved");
        }
    }

    public HeatSnapshotOverview? GetLatestHeatSnapshot(int sectorCount, int conceptCount)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            var batch = GetLatestHeatBatch(connection);
            return batch is null
                ? null
                : ReadHeatOverview(connection, batch, sectorCount, conceptCount);
        }
    }

    public HeatSnapshotOverview? GetHeatSnapshotAt(DateTimeOffset snapshotTime, int sectorCount, int conceptCount)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, snapshot_time, trade_date, sector_mapping_batch_id, concept_mapping_batch_id,
                       source, sector_count, concept_count, created_at
                FROM heat_snapshot_batches
                WHERE snapshot_time <= $snapshot_time
                ORDER BY snapshot_time DESC
                LIMIT 1;
                """;
            Add(command, "$snapshot_time", FormatDateTime(snapshotTime));
            using var reader = command.ExecuteReader();
            var batch = reader.Read() ? ReadHeatBatch(reader) : null;
            return batch is null
                ? null
                : ReadHeatOverview(connection, batch, sectorCount, conceptCount);
        }
    }

    private static void InsertSectorHeatRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        DateOnly tradeDate,
        string? mappingBatchId,
        SectorHeatSnapshot snapshot)
    {
        var rows = snapshot.SectorsByCode.Values
            .OrderByDescending(item => item.HeatScore)
            .ThenByDescending(item => item.TotalAmount)
            .ThenBy(item => item.SectorName, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => (Heat: item, Rank: index + 1))
            .ToArray();
        var rowIndex = 0;
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sector_heat_snapshots(
                    batch_id, row_index, snapshot_time, trade_date, mapping_batch_id,
                    sector_code, sector_name, heat_rank, stock_count, rising_count,
                    average_change_percent, rising_ratio_percent, total_amount, heat_score,
                    leaders_json, leader_symbols_json)
                VALUES(
                    $batch_id, $row_index, $snapshot_time, $trade_date, $mapping_batch_id,
                    $sector_code, $sector_name, $heat_rank, $stock_count, $rising_count,
                    $average_change_percent, $rising_ratio_percent, $total_amount, $heat_score,
                    $leaders_json, $leader_symbols_json);
                """;
            AddHeatParameters(
                command,
                batchId,
                rowIndex++,
                snapshot.SnapshotTime,
                tradeDate,
                mappingBatchId,
                row.Heat.SectorCode,
                row.Heat.SectorName,
                row.Rank,
                row.Heat.StockCount,
                row.Heat.RisingCount,
                row.Heat.AverageChangePercent,
                row.Heat.RisingRatioPercent,
                row.Heat.TotalAmount,
                row.Heat.HeatScore,
                row.Heat.Leaders,
                row.Heat.LeaderSymbols);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertConceptHeatRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        DateOnly tradeDate,
        string? mappingBatchId,
        ConceptHeatSnapshot snapshot)
    {
        var rows = snapshot.ConceptsByCode.Values
            .OrderByDescending(item => item.HeatScore)
            .ThenByDescending(item => item.TotalAmount)
            .ThenBy(item => item.ConceptName, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => (Heat: item, Rank: index + 1))
            .ToArray();
        var rowIndex = 0;
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO concept_heat_snapshots(
                    batch_id, row_index, snapshot_time, trade_date, mapping_batch_id,
                    concept_code, concept_name, heat_rank, stock_count, rising_count,
                    average_change_percent, rising_ratio_percent, total_amount, heat_score,
                    leaders_json, leader_symbols_json)
                VALUES(
                    $batch_id, $row_index, $snapshot_time, $trade_date, $mapping_batch_id,
                    $concept_code, $concept_name, $heat_rank, $stock_count, $rising_count,
                    $average_change_percent, $rising_ratio_percent, $total_amount, $heat_score,
                    $leaders_json, $leader_symbols_json);
                """;
            AddHeatParameters(
                command,
                batchId,
                rowIndex++,
                snapshot.SnapshotTime,
                tradeDate,
                mappingBatchId,
                row.Heat.ConceptCode,
                row.Heat.ConceptName,
                row.Rank,
                row.Heat.StockCount,
                row.Heat.RisingCount,
                row.Heat.AverageChangePercent,
                row.Heat.RisingRatioPercent,
                row.Heat.TotalAmount,
                row.Heat.HeatScore,
                row.Heat.Leaders,
                row.Heat.LeaderSymbols);
            command.ExecuteNonQuery();
        }
    }

    private static void AddHeatParameters(
        SqliteCommand command,
        string batchId,
        int rowIndex,
        DateTimeOffset snapshotTime,
        DateOnly tradeDate,
        string? mappingBatchId,
        string code,
        string name,
        int heatRank,
        int stockCount,
        int risingCount,
        decimal averageChangePercent,
        decimal risingRatioPercent,
        decimal totalAmount,
        decimal heatScore,
        IReadOnlyList<HeatLeader> leaders,
        IReadOnlyList<string> leaderSymbols)
    {
        Add(command, "$batch_id", batchId);
        Add(command, "$row_index", rowIndex);
        Add(command, "$snapshot_time", FormatDateTime(snapshotTime));
        Add(command, "$trade_date", FormatDate(tradeDate));
        Add(command, "$mapping_batch_id", (object?)mappingBatchId ?? DBNull.Value);
        Add(command, command.CommandText.Contains("$sector_code", StringComparison.Ordinal) ? "$sector_code" : "$concept_code", code);
        Add(command, command.CommandText.Contains("$sector_name", StringComparison.Ordinal) ? "$sector_name" : "$concept_name", name);
        Add(command, "$heat_rank", heatRank);
        Add(command, "$stock_count", stockCount);
        Add(command, "$rising_count", risingCount);
        Add(command, "$average_change_percent", FormatDecimal(averageChangePercent));
        Add(command, "$rising_ratio_percent", FormatDecimal(risingRatioPercent));
        Add(command, "$total_amount", FormatDecimal(totalAmount));
        Add(command, "$heat_score", FormatDecimal(heatScore));
        Add(command, "$leaders_json", JsonSerializer.Serialize(leaders, JsonOptions));
        Add(command, "$leader_symbols_json", JsonSerializer.Serialize(leaderSymbols, JsonOptions));
    }

    private static HeatSnapshotOverview ReadHeatOverview(
        SqliteConnection connection,
        HeatBatchRow batch,
        int sectorCount,
        int conceptCount)
    {
        return new HeatSnapshotOverview(
            batch.Id,
            batch.SnapshotTime,
            batch.TradeDate,
            batch.SectorMappingBatchId,
            batch.ConceptMappingBatchId,
            batch.SectorCount,
            batch.ConceptCount,
            ReadHeatItems(connection, "sector", batch.Id, Math.Clamp(sectorCount, 0, 5000)),
            ReadHeatItems(connection, "concept", batch.Id, Math.Clamp(conceptCount, 0, 5000)));
    }

    private static IReadOnlyList<HeatSnapshotItem> ReadHeatItems(
        SqliteConnection connection,
        string kind,
        string batchId,
        int count)
    {
        if (count == 0)
        {
            return [];
        }

        var table = kind == "sector" ? "sector_heat_snapshots" : "concept_heat_snapshots";
        var codeColumn = kind == "sector" ? "sector_code" : "concept_code";
        var nameColumn = kind == "sector" ? "sector_name" : "concept_name";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {codeColumn}, {nameColumn}, heat_rank, stock_count, rising_count,
                   average_change_percent, rising_ratio_percent, total_amount, heat_score,
                   leaders_json, leader_symbols_json
            FROM {table}
            WHERE batch_id = $batch_id
            ORDER BY heat_rank ASC
            LIMIT $count;
            """;
        Add(command, "$batch_id", batchId);
        Add(command, "$count", count);
        using var reader = command.ExecuteReader();
        var items = new List<HeatSnapshotItem>();
        while (reader.Read())
        {
            items.Add(new HeatSnapshotItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                ParseDecimal(reader.GetString(5)),
                ParseDecimal(reader.GetString(6)),
                ParseDecimal(reader.GetString(7)),
                ParseDecimal(reader.GetString(8)),
                JsonSerializer.Deserialize<HeatLeader[]>(reader.GetString(9), JsonOptions) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(10), JsonOptions) ?? []));
        }

        return items;
    }

    private static HeatBatchRow? GetLatestHeatBatch(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, snapshot_time, trade_date, sector_mapping_batch_id, concept_mapping_batch_id,
                   source, sector_count, concept_count, created_at
            FROM heat_snapshot_batches
            ORDER BY snapshot_time DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHeatBatch(reader) : null;
    }

    private static MappingSnapshotBatch? GetLatestMappingSnapshotCore(SqliteConnection connection, string mappingType)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mapping_type, snapshot_time, trade_date, source, item_count, file_hash, created_at
            FROM mapping_snapshot_batches
            WHERE mapping_type = $mapping_type
            ORDER BY snapshot_time DESC, created_at DESC
            LIMIT 1;
            """;
        Add(command, "$mapping_type", mappingType);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMappingBatch(reader) : null;
    }

    private static MappingSnapshotBatch ReadMappingBatch(SqliteDataReader reader)
    {
        return new MappingSnapshotBatch(
            reader.GetString(0),
            reader.GetString(1),
            ParseDateTime(reader.GetString(2)),
            ParseDate(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            ParseDateTime(reader.GetString(7)));
    }

    private static HeatBatchRow ReadHeatBatch(SqliteDataReader reader)
    {
        return new HeatBatchRow(
            reader.GetString(0),
            ParseDateTime(reader.GetString(1)),
            ParseDate(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            ParseDateTime(reader.GetString(8)));
    }

    private static string ComputeMappingHash(string mappingType, IReadOnlyList<MappingSnapshotItem> items)
    {
        var builder = new StringBuilder(mappingType);
        foreach (var item in items
                     .OrderBy(item => item.BoardCode, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append('|')
                .Append(item.BoardCode)
                .Append(',')
                .Append(item.BoardName)
                .Append(',')
                .Append(item.Symbol);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string NormalizeMappingType(string mappingType)
    {
        var value = mappingType.Trim().ToLowerInvariant();
        return value is "sector" or "concept"
            ? value
            : throw new ArgumentOutOfRangeException(nameof(mappingType), mappingType, "Mapping type must be sector or concept.");
    }

    private static void Add(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
    }

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string FormatDate(DateOnly value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateOnly ParseDate(string value)
    {
        return DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static decimal ParseDecimal(string value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }

    private sealed record HeatBatchRow(
        string Id,
        DateTimeOffset SnapshotTime,
        DateOnly TradeDate,
        string? SectorMappingBatchId,
        string? ConceptMappingBatchId,
        string Source,
        int SectorCount,
        int ConceptCount,
        DateTimeOffset CreatedAt);
}
