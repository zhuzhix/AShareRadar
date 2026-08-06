namespace AShareRadar.Application.Qlib;

public sealed class QlibSignalSyncService
{
    private readonly QlibSignalOptions _options;
    private readonly QlibSignalFileReader _reader;
    private readonly IQlibSignalSeedStore _seedStore;

    public QlibSignalSyncService(
        QlibSignalOptions options,
        QlibSignalFileReader reader,
        IQlibSignalSeedStore seedStore)
    {
        _options = options;
        _reader = reader;
        _seedStore = seedStore;
    }

    public QlibSignalStatus GetStatus()
    {
        return _reader.GetStatus();
    }

    public QlibSignalSnapshot GetLatest()
    {
        return _reader.LoadLatest();
    }

    public QlibSignalSnapshot GetRebalancePlan()
    {
        return _reader.LoadRebalancePlan();
    }

    public QlibSignalSeedImportResult ImportLatestSeeds()
    {
        return _seedStore.ImportSnapshot(_reader.LoadLatest());
    }

    public IReadOnlyList<QlibSignalSeed> QuerySeeds(DateOnly? signalDate, int? count)
    {
        return _seedStore.Query(signalDate, _options.StrategyCode, count ?? 200);
    }
}
