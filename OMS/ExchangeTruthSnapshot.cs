using System;

namespace OMS
{
    /// <summary>Authoritative snapshot used by QueryExchange / reconciliation.</summary>
    public sealed class ExchangeTruthSnapshot
    {
        public ExchangeTruthSnapshot(
            string clOrdId,
            bool orderExists,
            OrderState exchangeState,
            decimal orderQty,
            decimal cumQty,
            long lastExchangeSequence)
        {
            ClOrdId = clOrdId ?? throw new ArgumentNullException(nameof(clOrdId));
            OrderExists = orderExists;
            ExchangeState = exchangeState;
            OrderQty = orderQty;
            CumQty = cumQty;
            LastExchangeSequence = lastExchangeSequence;
        }

        public string ClOrdId { get; private set; }
        public bool OrderExists { get; private set; }
        public OrderState ExchangeState { get; private set; }
        public decimal OrderQty { get; private set; }
        public decimal CumQty { get; private set; }
        public long LastExchangeSequence { get; private set; }

        public decimal LeavesQty
        {
            get { return Math.Max(0, OrderQty - CumQty); }
        }
    }
}
