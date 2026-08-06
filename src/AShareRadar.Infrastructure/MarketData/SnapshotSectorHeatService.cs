using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class SnapshotSectorHeatService : ISectorHeatService
{
    private readonly object _mappingSync = new();
    private readonly string _sectorMappingPath;
    private readonly string _conceptMappingPath;
    private IReadOnlyDictionary<string, SectorMappingEntry> _sectorMappingBySymbol = new Dictionary<string, SectorMappingEntry>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<ConceptMappingEntry>> _conceptMappingsBySymbol = new Dictionary<string, IReadOnlyList<ConceptMappingEntry>>(StringComparer.OrdinalIgnoreCase);
    private SectorHeatMappingStatus _sectorMappingStatus;
    private SectorHeatMappingStatus _conceptMappingStatus;

    private static readonly IReadOnlyList<(string Code, string Name, string[] Keywords)> KeywordRules =
    [
        ("bank", "银行", ["银行"]),
        ("broker", "证券", ["证券", "券商"]),
        ("insurance", "保险", ["保险"]),
        ("real-estate", "房地产", ["地产", "置业", "城建"]),
        ("liquor-food", "食品饮料", ["酒", "茅台", "五粮液", "食品", "饮料", "乳业", "啤酒", "味业"]),
        ("medicine", "医药生物", ["药", "医", "医疗", "生物", "健康", "制药"]),
        ("new-energy", "新能源", ["锂", "光伏", "太阳", "能源", "电池", "宁德", "储能"]),
        ("auto", "汽车", ["汽车", "车", "汽配", "轮胎"]),
        ("semiconductor", "半导体", ["半导体", "芯片", "微", "集成", "电子"]),
        ("software-ai", "软件AI", ["软件", "信息", "数据", "智能", "科技", "网络", "传媒", "互联"]),
        ("telecom", "通信", ["通信", "电信", "移动", "联通", "光迅", "光缆"]),
        ("military", "军工航天", ["航天", "航空", "军工", "兵器", "中航"]),
        ("power", "电力", ["电力", "电网", "水电", "核电", "发电"]),
        ("coal-metal", "煤炭有色", ["煤", "矿", "铜", "铝", "锌", "黄金", "有色", "稀土", "钢铁"]),
        ("chemical", "化工", ["化工", "化学", "材料", "塑", "纤维", "石化"]),
        ("construction", "建筑基建", ["建筑", "建工", "交建", "铁建", "中铁", "路桥", "工程"]),
        ("machinery", "机械设备", ["机械", "装备", "机电", "重工", "机器人"]),
        ("consumer", "消费零售", ["商贸", "百货", "零售", "旅游", "酒店", "家居", "服饰"]),
        ("agriculture", "农业", ["农", "牧", "渔", "种业", "粮"]),
        ("transport", "交通运输", ["物流", "港", "机场", "航空", "铁路", "高速", "航运"]),
        ("environment", "环保", ["环保", "环境", "节能", "水务"]),
    ];

    private static readonly string[] DynamicConceptKeywords =
    [
        "昨日",
        "连板",
        "涨停",
        "新高",
        "超跌",
        "低价股",
        "微盘股",
        "百日",
        "近期",
        "破发",
        "破净",
        "预亏",
        "预增",
        "ST"
    ];

    public SnapshotSectorHeatService(MarketDataOptions options)
    {
        _sectorMappingPath = ResolvePath(options.SectorMappingPath);
        _conceptMappingPath = ResolvePath(options.ConceptMappingPath);
        _sectorMappingStatus = BuildMappingStatus(_sectorMappingPath, 0);
        _conceptMappingStatus = BuildMappingStatus(_conceptMappingPath, 0);
        ReloadMappings();
    }

    public SectorHeatSnapshot Build(MarketSnapshot snapshot)
    {
        IReadOnlyDictionary<string, SectorMappingEntry> sectorMappingBySymbol;
        lock (_mappingSync)
        {
            sectorMappingBySymbol = _sectorMappingBySymbol;
        }

        var quotes = snapshot.Quotes
            .Where(item => item.Price > 0)
            .ToArray();
        var memberships = quotes
            .Select(item => ClassifySector(item, sectorMappingBySymbol))
            .ToDictionary(item => item.Symbol, StringComparer.OrdinalIgnoreCase);

        var sectors = quotes
            .GroupBy(item => memberships[item.Symbol].SectorCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildSectorHeat(group.Key, memberships[group.First().Symbol].SectorName, group))
            .ToDictionary(item => item.SectorCode, StringComparer.OrdinalIgnoreCase);

        var heatBySymbol = memberships
            .Where(item => sectors.ContainsKey(item.Value.SectorCode))
            .ToDictionary(
                item => item.Key,
                item => sectors[item.Value.SectorCode],
                StringComparer.OrdinalIgnoreCase);

        return new SectorHeatSnapshot(snapshot.SnapshotTime, sectors, memberships, heatBySymbol);
    }

    public ConceptHeatSnapshot BuildConcepts(MarketSnapshot snapshot)
    {
        IReadOnlyDictionary<string, IReadOnlyList<ConceptMappingEntry>> conceptMappingsBySymbol;
        lock (_mappingSync)
        {
            conceptMappingsBySymbol = _conceptMappingsBySymbol;
        }

        var quotes = snapshot.Quotes
            .Where(item => item.Price > 0)
            .ToArray();
        var membershipsBySymbol = quotes
            .Select(item => new
            {
                item.Symbol,
                Memberships = ClassifyConcepts(item, conceptMappingsBySymbol)
            })
            .Where(item => item.Memberships.Count > 0)
            .ToDictionary(item => item.Symbol, item => item.Memberships, StringComparer.OrdinalIgnoreCase);

        var quoteBySymbol = quotes.ToDictionary(item => item.Symbol, StringComparer.OrdinalIgnoreCase);
        var concepts = membershipsBySymbol
            .SelectMany(item => item.Value.Select(membership => new
            {
                Membership = membership,
                Quote = quoteBySymbol[item.Key]
            }))
            .GroupBy(item => item.Membership.ConceptCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildConceptHeat(
                group.Key,
                group.First().Membership.ConceptName,
                group.Select(item => item.Quote)))
            .ToDictionary(item => item.ConceptCode, StringComparer.OrdinalIgnoreCase);

        var heatBySymbol = membershipsBySymbol
            .ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<ConceptHeat>)item.Value
                    .Where(membership => concepts.ContainsKey(membership.ConceptCode))
                    .Select(membership => concepts[membership.ConceptCode])
                    .OrderByDescending(heat => heat.HeatScore)
                    .ThenByDescending(heat => heat.TotalAmount)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new ConceptHeatSnapshot(snapshot.SnapshotTime, concepts, membershipsBySymbol, heatBySymbol);
    }

    public SectorHeatMappingStatus GetMappingStatus()
    {
        lock (_mappingSync)
        {
            return _sectorMappingStatus;
        }
    }

    public SectorHeatMappingStatus GetConceptMappingStatus()
    {
        lock (_mappingSync)
        {
            return _conceptMappingStatus;
        }
    }

    public void ReloadMappings()
    {
        var sectorMappingBySymbol = LoadSectorMappings(_sectorMappingPath);
        var conceptMappingsBySymbol = LoadConceptMappings(_conceptMappingPath);
        lock (_mappingSync)
        {
            _sectorMappingBySymbol = sectorMappingBySymbol;
            _sectorMappingStatus = BuildMappingStatus(_sectorMappingPath, sectorMappingBySymbol.Count);
            _conceptMappingsBySymbol = conceptMappingsBySymbol;
            _conceptMappingStatus = BuildMappingStatus(
                _conceptMappingPath,
                conceptMappingsBySymbol.Sum(item => item.Value.Count));
        }
    }

    private SectorMembership ClassifySector(
        StockQuote quote,
        IReadOnlyDictionary<string, SectorMappingEntry> sectorMappingBySymbol)
    {
        var normalized = StockSymbolNormalizer.NormalizeCode(quote.Symbol);
        if (sectorMappingBySymbol.TryGetValue(normalized, out var mapping))
        {
            return new SectorMembership(quote.Symbol, mapping.Code, mapping.Name, "CsvMapping");
        }

        var name = quote.Name ?? string.Empty;
        foreach (var (code, sectorName, keywords) in KeywordRules)
        {
            if (keywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return new SectorMembership(quote.Symbol, code, sectorName, "KeywordFallback");
            }
        }

        if (normalized.StartsWith("688", StringComparison.Ordinal))
        {
            return new SectorMembership(quote.Symbol, "star-market", "科创板", "BoardFallback");
        }

        if (normalized.StartsWith("300", StringComparison.Ordinal))
        {
            return new SectorMembership(quote.Symbol, "chinext", "创业板", "BoardFallback");
        }

        return new SectorMembership(quote.Symbol, "main-board", "主板综合", "BoardFallback");
    }

    private IReadOnlyList<ConceptMembership> ClassifyConcepts(
        StockQuote quote,
        IReadOnlyDictionary<string, IReadOnlyList<ConceptMappingEntry>> conceptMappingsBySymbol)
    {
        var normalized = StockSymbolNormalizer.NormalizeCode(quote.Symbol);
        if (conceptMappingsBySymbol.TryGetValue(normalized, out var mappings))
        {
            return mappings
                .Select(item => new ConceptMembership(quote.Symbol, item.Code, item.Name, "CsvMapping"))
                .ToArray();
        }

        return [];
    }

    private static IReadOnlyDictionary<string, SectorMappingEntry> LoadSectorMappings(string mappingPath)
    {
        if (!File.Exists(mappingPath))
        {
            return new Dictionary<string, SectorMappingEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var mappings = new Dictionary<string, SectorMappingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var columns in ReadCsvRows(mappingPath))
        {
            if (columns.Count < 3)
            {
                continue;
            }

            var symbol = StockSymbolNormalizer.NormalizeCode(columns[0]);
            var code = columns[1].Trim();
            var name = columns[2].Trim();
            if (IsValidMapping(symbol, code, name))
            {
                mappings[symbol] = new SectorMappingEntry(code, name);
            }
        }

        return mappings;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ConceptMappingEntry>> LoadConceptMappings(string mappingPath)
    {
        if (!File.Exists(mappingPath))
        {
            return new Dictionary<string, IReadOnlyList<ConceptMappingEntry>>(StringComparer.OrdinalIgnoreCase);
        }

        var mappings = new Dictionary<string, List<ConceptMappingEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var columns in ReadCsvRows(mappingPath))
        {
            if (columns.Count < 3)
            {
                continue;
            }

            var symbol = StockSymbolNormalizer.NormalizeCode(columns[0]);
            var code = columns[1].Trim();
            var name = columns[2].Trim();
            if (!IsValidMapping(symbol, code, name) || IsDynamicConcept(name))
            {
                continue;
            }

            if (!mappings.TryGetValue(symbol, out var items))
            {
                items = [];
                mappings[symbol] = items;
            }

            if (items.All(item => !string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new ConceptMappingEntry(code, name));
            }
        }

        return mappings.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<ConceptMappingEntry>)item.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<IReadOnlyList<string>> ReadCsvRows(string mappingPath)
    {
        foreach (var line in File.ReadLines(mappingPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            yield return ParseCsvLine(line);
        }
    }

    private static bool IsValidMapping(string symbol, string code, string name) =>
        !string.IsNullOrWhiteSpace(symbol) &&
        !string.IsNullOrWhiteSpace(code) &&
        !string.IsNullOrWhiteSpace(name);

    private static bool IsDynamicConcept(string conceptName) =>
        DynamicConceptKeywords.Any(keyword => conceptName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static SectorHeatMappingStatus BuildMappingStatus(string path, int count) =>
        new(path, count, count == 0 ? null : DateTimeOffset.Now, count == 0 ? "FallbackOnly" : "CsvMapping");

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString().Trim());
        return values;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static SectorHeat BuildSectorHeat(
        string sectorCode,
        string sectorName,
        IEnumerable<StockQuote> quoteSource)
    {
        var quotes = quoteSource.ToArray();
        var common = BuildCommonHeat(quotes);

        return new SectorHeat(
            sectorCode,
            sectorName,
            common.StockCount,
            common.RisingCount,
            common.AverageChange,
            common.RisingRatio,
            common.TotalAmount,
            common.HeatScore,
            common.Leaders,
            common.LeaderSymbols);
    }

    private static ConceptHeat BuildConceptHeat(
        string conceptCode,
        string conceptName,
        IEnumerable<StockQuote> quoteSource)
    {
        var quotes = quoteSource
            .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var common = BuildCommonHeat(quotes);

        return new ConceptHeat(
            conceptCode,
            conceptName,
            common.StockCount,
            common.RisingCount,
            common.AverageChange,
            common.RisingRatio,
            common.TotalAmount,
            common.HeatScore,
            common.Leaders,
            common.LeaderSymbols);
    }

    private static CommonHeat BuildCommonHeat(IReadOnlyList<StockQuote> quotes)
    {
        var stockCount = quotes.Count;
        var risingCount = quotes.Count(item => item.ChangePercent > 0);
        var averageChange = stockCount == 0 ? 0 : quotes.Average(item => item.ChangePercent);
        var risingRatio = stockCount == 0 ? 0 : risingCount * 100m / stockCount;
        var totalAmount = quotes.Sum(item => item.Amount);
        var leaders = quotes
            .OrderByDescending(item => item.ChangePercent)
            .ThenByDescending(item => item.Amount)
            .Take(5)
            .Select((item, index) => new HeatLeader(
                index + 1,
                StockSymbolNormalizer.NormalizeCode(item.Symbol),
                item.Name,
                Math.Round(item.ChangePercent, 2),
                Math.Round(item.Amount, 0),
                Math.Round(item.VolumeRatio, 2)))
            .ToArray();
        var leaderSymbols = leaders
            .Take(3)
            .Select(item => $"{item.Symbol} {item.Name}")
            .ToArray();

        var amountScore = Math.Min(totalAmount / 1_000_000_000m, 24m);
        var breadthScore = risingRatio * 0.28m;
        var changeScore = Math.Max(averageChange, -5m) * 6m;
        var sizeScore = Math.Min(stockCount, 40) * 0.2m;
        var heatScore = Math.Round(Math.Clamp(50m + changeScore + breadthScore + amountScore + sizeScore, 0m, 100m), 2);

        return new CommonHeat(
            stockCount,
            risingCount,
            Math.Round(averageChange, 2),
            Math.Round(risingRatio, 2),
            Math.Round(totalAmount, 0),
            heatScore,
            leaders,
            leaderSymbols);
    }

    private sealed record SectorMappingEntry(string Code, string Name);

    private sealed record ConceptMappingEntry(string Code, string Name);

    private sealed record CommonHeat(
        int StockCount,
        int RisingCount,
        decimal AverageChange,
        decimal RisingRatio,
        decimal TotalAmount,
        decimal HeatScore,
        IReadOnlyList<HeatLeader> Leaders,
        IReadOnlyList<string> LeaderSymbols);
}
