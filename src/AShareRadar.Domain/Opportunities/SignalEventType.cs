namespace AShareRadar.Domain.Opportunities;

public enum SignalEventType
{
    New = 0,
    Continued = 1,
    ReHit = 2,
    Strengthened = 3,
    Weakened = 4,
    Disappeared = 5,
    ManualMarked = 6
}
