using System;

namespace OMS
{
    /// <summary>Terminal and non-terminal states for the order lifecycle.</summary>
    public enum OrderState
    {
        New,
        PendingAck,
        Open,
        /// <summary>Resting with executions; remainder may be cancelled by user or venue.</summary>
        PartiallyFilled,
        /// <summary>Amend/replace in flight (single venue replace — not cancel-then-new).</summary>
        PendingReplace,
        PendingCancel,
        Filled,
        Cancelled,
        Rejected,
        StatusUnknown
    }
}
