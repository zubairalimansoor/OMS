using System;

namespace OMS
{
    public enum ExchangeMessageKind
    {
        NewOrderAck,
        NewOrderReject,
        PartialFill,
        FullFill,
        CancelAck,
        CancelReject,
        ExchangeCancelled,
        ReplaceAck,
        ReplaceReject
    }

    /// <summary>Base type for all async responses from the simulated exchange.</summary>
    public abstract class ExchangeMessage
    {
        protected ExchangeMessage(long sequenceNumber, string clOrdId, ExchangeMessageKind kind)
        {
            SequenceNumber = sequenceNumber;
            ClOrdId = clOrdId ?? throw new ArgumentNullException(nameof(clOrdId));
            Kind = kind;
        }

        public long SequenceNumber { get; private set; }
        public string ClOrdId { get; private set; }
        public ExchangeMessageKind Kind { get; private set; }
    }

    public sealed class NewOrderAckMessage : ExchangeMessage
    {
        public NewOrderAckMessage(long sequenceNumber, string clOrdId)
            : base(sequenceNumber, clOrdId, ExchangeMessageKind.NewOrderAck)
        {
        }
    }

    public sealed class NewOrderRejectMessage : ExchangeMessage
    {
        public NewOrderRejectMessage(long sequenceNumber, string clOrdId, string reason)
            : base(sequenceNumber, clOrdId, ExchangeMessageKind.NewOrderReject)
        {
            Reason = reason ?? string.Empty;
        }

        public string Reason { get; private set; }
    }

    public sealed class FillMessage : ExchangeMessage
    {
        public FillMessage(long sequenceNumber, string clOrdId, string execId, decimal lastQty, decimal cumQty, bool isLastFill)
            : base(sequenceNumber, clOrdId, isLastFill ? ExchangeMessageKind.FullFill : ExchangeMessageKind.PartialFill)
        {
            ExecId = execId ?? throw new ArgumentNullException(nameof(execId));
            LastQty = lastQty;
            CumQty = cumQty;
            IsLastFill = isLastFill;
        }

        /// <summary>Idempotency key fragment: stable per logical execution report.</summary>
        public string ExecId { get; private set; }

        public decimal LastQty { get; private set; }
        public decimal CumQty { get; private set; }
        public bool IsLastFill { get; private set; }
    }

    public sealed class CancelAckMessage : ExchangeMessage
    {
        public CancelAckMessage(long sequenceNumber, string clOrdId)
            : base(sequenceNumber, clOrdId, ExchangeMessageKind.CancelAck)
        {
        }
    }

    public sealed class CancelRejectMessage : ExchangeMessage
    {
        public CancelRejectMessage(long sequenceNumber, string clOrdId, string reason)
            : base(sequenceNumber, clOrdId, ExchangeMessageKind.CancelReject)
        {
            Reason = reason ?? string.Empty;
        }

        public string Reason { get; private set; }
    }

    public sealed class ExchangeCancelledMessage : ExchangeMessage
    {
        public ExchangeCancelledMessage(long sequenceNumber, string clOrdId, string reason)
            : base(sequenceNumber, clOrdId, ExchangeMessageKind.ExchangeCancelled)
        {
            Reason = reason ?? string.Empty;
        }

        public string Reason { get; private set; }
    }

    public sealed class ReplaceAckMessage : ExchangeMessage
    {
        public ReplaceAckMessage(long sequenceNumber, string clOrdId, decimal newOrderQty)
            : base(sequenceNumber, clOrdId, ExchangeMessageKind.ReplaceAck)
        {
            NewOrderQty = newOrderQty;
        }

        public decimal NewOrderQty { get; private set; }
    }

    public sealed class ReplaceRejectMessage : ExchangeMessage
    {
        public ReplaceRejectMessage(long sequenceNumber, string clOrdId, string reason)
            : base(sequenceNumber, clOrdId, ExchangeMessageKind.ReplaceReject)
        {
            Reason = reason ?? string.Empty;
        }

        public string Reason { get; private set; }
    }
}
