namespace PointOfSale.Core.Enums;

public enum CommerceHubPayoutStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    ReconciliationRequired = 4,
    /// <summary>Development internal accounting terminal state. Never bank/provider completion.</summary>
    SimulatedCompleted = 5
}
