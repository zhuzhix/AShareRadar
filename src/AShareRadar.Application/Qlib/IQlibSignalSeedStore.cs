namespace AShareRadar.Application.Qlib;

public interface IQlibSignalSeedStore
{
    QlibSignalSeedImportResult ImportSnapshot(QlibSignalSnapshot snapshot);

    IReadOnlyList<QlibSignalSeed> Query(DateOnly? signalDate, string? strategyCode, int count);
}