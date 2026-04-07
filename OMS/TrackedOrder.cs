using System;
using System.Collections.Generic;

namespace OMS
{
    /// <summary>In-memory order tracked by the OMS (one ClOrdId).</summary>
    public sealed class TrackedOrder
    {
        public TrackedOrder(string clOrdId, decimal orderQty)
        {
            ClOrdId = clOrdId ?? throw new ArgumentNullException(nameof(clOrdId));
            OrderQty = orderQty;
            State = OrderState.New;
            SyncRoot = new object();
            AppliedMessageSequences = new HashSet<long>();
            ProcessedExecIds = new HashSet<string>();
        }

        public string ClOrdId { get; private set; }
        public decimal OrderQty { get; set; }
        public decimal CumQty { get; set; }
        public OrderState State { get; set; }

        public string LastRejectReason { get; set; }

        /// <summary>Seriation: all exchange messages applied for this order.</summary>
        public HashSet<long> AppliedMessageSequences { get; private set; }

        /// <summary>Fill reports once applied (idempotency).</summary>
        public HashSet<string> ProcessedExecIds { get; private set; }

        public DateTime? PendingAckSince { get; set; }
        public DateTime? PendingCancelSince { get; set; }

        public DateTime? PendingReplaceSince { get; set; }

        /// <summary>Target display quantity after in-flight replace ack (optional bookkeeping).</summary>
        public decimal? PendingReplaceTargetQty { get; set; }

        public object SyncRoot { get; private set; }

        public decimal LeavesQty
        {
            get { return Math.Max(0, OrderQty - CumQty); }
        }

        public bool IsTerminal()
        {
            return State == OrderState.Filled
                || State == OrderState.Cancelled
                || State == OrderState.Rejected;
        }
    }
}
