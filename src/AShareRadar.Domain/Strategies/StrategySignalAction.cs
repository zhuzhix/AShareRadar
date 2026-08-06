namespace AShareRadar.Domain.Strategies;

public enum StrategySignalAction
{
    Watch = 0,
    Candidate = 1,
    PullbackWait = 2,
    Confirm = 3,
    Reject = 4
}
