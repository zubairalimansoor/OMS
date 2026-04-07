using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OMS
{
    /// <summary>Single-exchange simulator with latency, drops, unilateral cancels, and async fills.</summary>
    public sealed class SimulatedExchange : IExchangeGateway
    {
        private readonly IInboundExchangeSink _inboundSink;
        private readonly Random _random;
        private readonly ConcurrentDictionary<string, ExchangeOrder> _orders = new ConcurrentDictionary<string, ExchangeOrder>(StringComparer.Ordinal);
        private long _sequence;
        private long _execCounter;

        public TimeSpan BaseLatency { get; set; }

        public int FillExtraDelayMilliseconds { get; set; }

        public double DropOutboundProbability { get; set; }

        public double UnilateralCancelProbability { get; set; }

        public bool ForceDropNextNewOrderAck { get; set; }

        public bool ForceDropNextFill { get; set; }

        public bool SuppressUnilateralNotifications { get; set; }

        /// <summary>When true (default), the simulator eventually sends one full fill after the ack path. Turn off for staged partial fills.</summary>
        public bool EnableAutomaticFill { get; set; }

        /// <summary>Next trader cancel results in CancelReject to the OMS without mutating book cancel state (venue declined cancel).</summary>
        public bool ForceNextCancelReject { get; set; }

        public string ForceNextCancelRejectReason { get; set; }

        public SimulatedExchange(IInboundExchangeSink inboundSink, Random random, TimeSpan baseLatency)
        {
            _inboundSink = inboundSink ?? throw new ArgumentNullException(nameof(inboundSink));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            BaseLatency = baseLatency;
            EnableAutomaticFill = true;
            ForceNextCancelRejectReason = "Venue declined cancel (simulated).";
        }

        private long NextSeq()
        {
            return Interlocked.Increment(ref _sequence);
        }

        private string NextExecId()
        {
            var n = Interlocked.Increment(ref _execCounter);
            return "E" + n.ToString(CultureInfo.InvariantCulture);
        }

        public ExchangeTruthSnapshot QueryExchange(string clOrdId)
        {
            ExchangeOrder o;
            if (!_orders.TryGetValue(clOrdId, out o))
            {
                return new ExchangeTruthSnapshot(clOrdId, false, OrderState.Rejected, 0, 0, 0);
            }

            lock (o.SyncRoot)
            {
                var state = MapExchangeOrderState(o);
                return new ExchangeTruthSnapshot(clOrdId, true, state, o.OrderQty, o.CumQty, 0);
            }
        }

        private static OrderState MapExchangeOrderState(ExchangeOrder o)
        {
            if (o.NewOrderRejected)
            {
                return OrderState.Rejected;
            }

            if (!o.NewOrderAccepted)
            {
                return OrderState.PendingAck;
            }

            if (o.CumQty >= o.OrderQty && o.OrderQty > 0)
            {
                return OrderState.Filled;
            }

            if (o.UnilateralCancelSilent || o.CancelledByTrader)
            {
                return OrderState.Cancelled;
            }

            if (o.CumQty > 0)
            {
                return OrderState.PartiallyFilled;
            }

            return OrderState.Open;
        }

        public async Task SubmitNewOrderAsync(string clOrdId, decimal quantity)
        {
            var order = new ExchangeOrder(clOrdId, quantity);
            if (!_orders.TryAdd(clOrdId, order))
            {
                return;
            }

            await DelayJittered().ConfigureAwait(false);

            var dropAck = ForceDropNextNewOrderAck;
            ForceDropNextNewOrderAck = false;

            lock (order.SyncRoot)
            {
                order.NewOrderAccepted = true;
            }

            if (!dropAck && !ShouldDrop())
            {
                PostToOms(new NewOrderAckMessage(NextSeq(), clOrdId));
            }

            _ = MaybeUnilateralCancelAsync(order);
            if (EnableAutomaticFill)
            {
                _ = ScheduleFillsAsync(order);
            }
        }

        public async Task SubmitReplaceAsync(string clOrdId, decimal newOrderQty)
        {
            await DelayJittered().ConfigureAwait(false);

            ExchangeOrder o;
            if (!_orders.TryGetValue(clOrdId, out o))
            {
                if (!ShouldDrop())
                {
                    PostToOms(new ReplaceRejectMessage(NextSeq(), clOrdId, "Order not found at exchange."));
                }

                return;
            }

            var rejectReason = default(string);
            var newQty = default(decimal);
            var ok = false;

            lock (o.SyncRoot)
            {
                if (!o.NewOrderAccepted)
                {
                    rejectReason = "Order not active on book.";
                }
                else if (o.NewOrderRejected)
                {
                    rejectReason = "Order was rejected.";
                }
                else if (o.UnilateralCancelSilent || o.CancelledByTrader)
                {
                    rejectReason = "Order is not working at exchange.";
                }
                else if (newOrderQty < o.CumQty)
                {
                    rejectReason = "Replace quantity is below accumulated fill.";
                }
                else
                {
                    o.OrderQty = newOrderQty;
                    newQty = o.OrderQty;
                    ok = true;
                }
            }

            if (!ok)
            {
                if (!ShouldDrop())
                {
                    PostToOms(new ReplaceRejectMessage(NextSeq(), clOrdId, rejectReason ?? "Replace rejected."));
                }

                return;
            }

            if (ShouldDrop())
            {
                return;
            }

            PostToOms(new ReplaceAckMessage(NextSeq(), clOrdId, newQty));
        }

        public async Task SubmitCancelAsync(string clOrdId)
        {
            await DelayJittered().ConfigureAwait(false);

            if (ForceNextCancelReject)
            {
                ForceNextCancelReject = false;
                var reason = string.IsNullOrEmpty(ForceNextCancelRejectReason)
                    ? "Venue declined cancel (simulated)."
                    : ForceNextCancelRejectReason;
                if (!ShouldDrop())
                {
                    PostToOms(new CancelRejectMessage(NextSeq(), clOrdId, reason));
                }

                return;
            }

            ExchangeOrder o;
            if (!_orders.TryGetValue(clOrdId, out o))
            {
                if (!ShouldDrop())
                {
                    PostToOms(new CancelRejectMessage(NextSeq(), clOrdId, "Order not found at exchange."));
                }

                return;
            }

            bool sendAck;
            string rejectReason;
            lock (o.SyncRoot)
            {
                sendAck = TryApplyTraderCancelLocked(o, out rejectReason);
            }

            if (ShouldDrop())
            {
                return;
            }

            if (sendAck)
            {
                PostToOms(new CancelAckMessage(NextSeq(), clOrdId));
            }
            else if (rejectReason != null)
            {
                PostToOms(new CancelRejectMessage(NextSeq(), clOrdId, rejectReason));
            }
        }

        /// <summary>Publishes one execution report chunk (partial or final) for harness-driven partial fill sequences.</summary>
        public async Task PublishFillSliceAsync(string clOrdId, decimal lastQty, int delayBeforeMs = 0)
        {
            if (delayBeforeMs > 0)
            {
                await Task.Delay(delayBeforeMs).ConfigureAwait(false);
            }

            ExchangeOrder o;
            if (!_orders.TryGetValue(clOrdId, out o))
            {
                return;
            }

            var execId = default(string);
            var lastPosted = default(decimal);
            var cumPosted = default(decimal);
            var isLast = false;
            var shouldPublish = false;

            lock (o.SyncRoot)
            {
                if (!o.NewOrderAccepted || o.NewOrderRejected || o.UnilateralCancelSilent || o.CancelledByTrader)
                {
                    return;
                }

                var leaves = o.OrderQty - o.CumQty;
                if (leaves <= 0)
                {
                    return;
                }

                var slice = lastQty < leaves ? lastQty : leaves;
                if (slice <= 0)
                {
                    return;
                }

                execId = NextExecId();
                o.CumQty = o.CumQty + slice;
                o.LastFillQty = slice;
                lastPosted = slice;
                cumPosted = o.CumQty;
                isLast = o.CumQty >= o.OrderQty;
                shouldPublish = true;
            }

            if (shouldPublish)
            {
                PostToOms(new FillMessage(NextSeq(), clOrdId, execId, lastPosted, cumPosted, isLast));
            }
        }

        /// <summary>Returns true when the venue accepts the cancel (mutates book). When false, rejectReason is non-null for messaging.</summary>
        private static bool TryApplyTraderCancelLocked(ExchangeOrder o, out string rejectReason)
        {
            rejectReason = null;

            if (!o.NewOrderAccepted)
            {
                rejectReason = "Order not active on book.";
                return false;
            }

            if (o.NewOrderRejected)
            {
                rejectReason = "Order was rejected.";
                return false;
            }

            if (o.UnilateralCancelSilent)
            {
                rejectReason = "Order already removed by exchange.";
                return false;
            }

            if (o.CumQty >= o.OrderQty && o.OrderQty > 0)
            {
                rejectReason = "Already filled.";
                return false;
            }

            if (o.CancelledByTrader)
            {
                rejectReason = "Already cancelled.";
                return false;
            }

            o.CancelledByTrader = true;
            return true;
        }

        private async Task ScheduleFillsAsync(ExchangeOrder order)
        {
            var extra = FillExtraDelayMilliseconds;
            if (extra > 0)
            {
                await Task.Delay(extra).ConfigureAwait(false);
            }

            await DelayJittered().ConfigureAwait(false);

            var clOrdId = default(string);
            var execId = default(string);
            var lastQty = default(decimal);
            var cumQty = default(decimal);
            var shouldPublish = false;

            lock (order.SyncRoot)
            {
                if (!order.NewOrderAccepted || order.NewOrderRejected || order.UnilateralCancelSilent || order.CancelledByTrader)
                {
                    return;
                }

                var qtyLeft = order.OrderQty - order.CumQty;
                if (qtyLeft <= 0)
                {
                    return;
                }

                var dropFill = ForceDropNextFill;
                ForceDropNextFill = false;
                if (dropFill || ShouldDrop())
                {
                    return;
                }

                execId = NextExecId();
                order.CumQty = order.OrderQty;
                order.LastFillQty = qtyLeft;
                clOrdId = order.ClOrdId;
                lastQty = qtyLeft;
                cumQty = order.OrderQty;
                shouldPublish = true;
            }

            if (shouldPublish)
            {
                PostToOms(new FillMessage(NextSeq(), clOrdId, execId, lastQty, cumQty, true));
            }
        }

        private async Task MaybeUnilateralCancelAsync(ExchangeOrder order)
        {
            if (UnilateralCancelProbability <= 0 || _random.NextDouble() > UnilateralCancelProbability)
            {
                return;
            }

            await DelayJittered().ConfigureAwait(false);

            bool notify;
            lock (order.SyncRoot)
            {
                if (order.CumQty >= order.OrderQty || order.CancelledByTrader)
                {
                    return;
                }

                order.UnilateralCancelSilent = true;
                notify = !SuppressUnilateralNotifications;
            }

            if (notify)
            {
                PostToOms(new ExchangeCancelledMessage(NextSeq(), order.ClOrdId, "Unilateral exchange cancel (simulated)."));
            }
        }

        private async Task DelayJittered()
        {
            var jitterHi = Math.Max(1, (int)BaseLatency.TotalMilliseconds / 4);
            var jitter = _random.Next(0, jitterHi);
            var ms = (int)BaseLatency.TotalMilliseconds + jitter;
            if (ms > 0)
            {
                await Task.Delay(ms).ConfigureAwait(false);
            }
        }

        private bool ShouldDrop()
        {
            return DropOutboundProbability > 0 && _random.NextDouble() < DropOutboundProbability;
        }

        private void PostToOms(ExchangeMessage message)
        {
            if (message == null)
            {
                return;
            }

            _inboundSink.OnMessageReceived(message);
        }

        private sealed class ExchangeOrder
        {
            public ExchangeOrder(string clOrdId, decimal orderQty)
            {
                ClOrdId = clOrdId;
                OrderQty = orderQty;
                SyncRoot = new object();
            }

            public string ClOrdId { get; private set; }
            public decimal OrderQty { get; set; }
            public decimal CumQty { get; set; }
            public decimal LastFillQty { get; set; }
            public bool NewOrderAccepted { get; set; }
            public bool NewOrderRejected { get; set; }
            public bool CancelledByTrader { get; set; }
            public bool UnilateralCancelSilent { get; set; }
            public object SyncRoot { get; private set; }
        }
    }
}
