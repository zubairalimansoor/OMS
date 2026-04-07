using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace OMS
{
    /// <summary>Core OMS: concurrent order book, async message handling, watchdog reconciliation.</summary>
    public sealed class OmsEngine : IOmsEngine, IInboundExchangeSink
    {
        private static readonly TimeSpan PendingThreshold = TimeSpan.FromMilliseconds(500);

        private readonly IExchangeGateway _gateway;
        private readonly ConcurrentDictionary<string, TrackedOrder> _orders =
            new ConcurrentDictionary<string, TrackedOrder>(StringComparer.Ordinal);

        private readonly Timer _watchdog;
        private long _clOrdIdCounter;

        public OmsEngine(IExchangeGateway gateway)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _watchdog = new Timer(WatchdogTick, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }

        public event EventHandler OrderChanged;

        public async Task<string> SubmitNewOrderAsync(decimal quantity)
        {
            var clOrdId = NextClOrdId();
            var order = new TrackedOrder(clOrdId, quantity);

            if (!_orders.TryAdd(clOrdId, order))
            {
                throw new InvalidOperationException("ClOrdID collision (should not happen).");
            }

            lock (order.SyncRoot)
            {
                EnsureState(order, OrderState.New);
                order.State = OrderState.PendingAck;
                order.PendingAckSince = DateTime.UtcNow;
                order.PendingCancelSince = null;
            }

            RaiseChanged();
            await _gateway.SubmitNewOrderAsync(clOrdId, quantity).ConfigureAwait(false);
            return clOrdId;
        }

        public async Task SubmitCancelAsync(string clOrdId)
        {
            TrackedOrder order = GetOrderOrThrow(clOrdId);

            lock (order.SyncRoot)
            {
                if (order.IsTerminal())
                {
                    return;
                }

                if (order.State == OrderState.PendingCancel)
                {
                    return;
                }

                if (order.State == OrderState.PendingReplace)
                {
                    return;
                }

                if (order.State != OrderState.Open
                    && order.State != OrderState.PartiallyFilled
                    && order.State != OrderState.PendingAck
                    && order.State != OrderState.StatusUnknown)
                {
                    return;
                }

                order.State = OrderState.PendingCancel;
                order.PendingCancelSince = DateTime.UtcNow;
                order.PendingAckSince = null;
            }

            RaiseChanged();
            await _gateway.SubmitCancelAsync(clOrdId).ConfigureAwait(false);
        }

        public async Task SubmitReplaceAsync(string clOrdId, decimal newOrderQty)
        {
            TrackedOrder order = GetOrderOrThrow(clOrdId);

            if (newOrderQty <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newOrderQty));
            }

            lock (order.SyncRoot)
            {
                if (order.IsTerminal())
                {
                    return;
                }

                if (order.State == OrderState.PendingCancel || order.State == OrderState.PendingReplace)
                {
                    return;
                }

                if (order.State != OrderState.Open && order.State != OrderState.PartiallyFilled)
                {
                    return;
                }

                order.State = OrderState.PendingReplace;
                order.PendingReplaceSince = DateTime.UtcNow;
                order.PendingReplaceTargetQty = newOrderQty;
                order.PendingAckSince = null;
            }

            RaiseChanged();
            await _gateway.SubmitReplaceAsync(clOrdId, newOrderQty).ConfigureAwait(false);
        }

        public void OnMessageReceived(ExchangeMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            TrackedOrder order;
            if (!_orders.TryGetValue(message.ClOrdId, out order))
            {
                return;
            }

            lock (order.SyncRoot)
            {
                ApplyExchangeMessage(order, message);
            }

            RaiseChanged();
        }

        public TrackedOrder TryGetOrder(string clOrdId)
        {
            TrackedOrder o;
            return _orders.TryGetValue(clOrdId, out o) ? o : null;
        }

        public IReadOnlyDictionary<string, TrackedOrder> SnapshotOrders()
        {
            return new ReadOnlyDictionary<string, TrackedOrder>(_orders);
        }

        private void WatchdogTick(object state)
        {
            foreach (var kv in _orders)
            {
                var order = kv.Value;
                var now = DateTime.UtcNow;
                bool triggered = false;

                lock (order.SyncRoot)
                {
                    if (order.IsTerminal())
                    {
                        continue;
                    }

                    if (order.State == OrderState.PendingAck && order.PendingAckSince.HasValue
                        && now - order.PendingAckSince.Value > PendingThreshold)
                    {
                        TransitionToStatusUnknown(order);
                        triggered = true;
                    }
                    else if (order.State == OrderState.PendingCancel && order.PendingCancelSince.HasValue
                        && now - order.PendingCancelSince.Value > PendingThreshold)
                    {
                        TransitionToStatusUnknown(order);
                        triggered = true;
                    }
                    else if (order.State == OrderState.PendingReplace && order.PendingReplaceSince.HasValue
                        && now - order.PendingReplaceSince.Value > PendingThreshold)
                    {
                        TransitionToStatusUnknown(order);
                        triggered = true;
                    }
                }

                if (triggered)
                {
                    RaiseChanged();
                    RunReconciliation(order.ClOrdId);
                }
            }
        }

        private void TransitionToStatusUnknown(TrackedOrder order)
        {
            order.State = OrderState.StatusUnknown;
            order.PendingAckSince = null;
            order.PendingCancelSince = null;
            order.PendingReplaceSince = null;
            order.PendingReplaceTargetQty = null;
        }

        private void RunReconciliation(string clOrdId)
        {
            TrackedOrder order;
            if (!_orders.TryGetValue(clOrdId, out order))
            {
                return;
            }

            ExchangeTruthSnapshot snapshot = null;
            try
            {
                snapshot = _gateway.QueryExchange(clOrdId);
            }
            catch
            {
                return;
            }

            if (snapshot == null)
            {
                return;
            }

            lock (order.SyncRoot)
            {
                ReconciliationMerge.Merge(order, snapshot);
            }

            RaiseChanged();
        }

        private void ApplyExchangeMessage(TrackedOrder order, ExchangeMessage message)
        {
            if (order.AppliedMessageSequences.Contains(message.SequenceNumber))
            {
                return;
            }

            var fill = message as FillMessage;
            if (fill != null)
            {
                if (order.ProcessedExecIds.Contains(fill.ExecId))
                {
                    order.AppliedMessageSequences.Add(message.SequenceNumber);
                    return;
                }
            }

            if (fill != null)
            {
                ApplyFill(order, fill, message.SequenceNumber);
                return;
            }

            var ack = message as NewOrderAckMessage;
            if (ack != null)
            {
                ApplyNewOrderAck(order, message.SequenceNumber);
                return;
            }

            var rej = message as NewOrderRejectMessage;
            if (rej != null)
            {
                ApplyNewOrderReject(order, rej, message.SequenceNumber);
                return;
            }

            var cack = message as CancelAckMessage;
            if (cack != null)
            {
                ApplyCancelAck(order, message.SequenceNumber);
                return;
            }

            var crej = message as CancelRejectMessage;
            if (crej != null)
            {
                ApplyCancelReject(order, crej, message.SequenceNumber);
                return;
            }

            var exchCx = message as ExchangeCancelledMessage;
            if (exchCx != null)
            {
                ApplyExchangeCancelled(order, exchCx, message.SequenceNumber);
                return;
            }

            var replAck = message as ReplaceAckMessage;
            if (replAck != null)
            {
                ApplyReplaceAck(order, replAck, message.SequenceNumber);
                return;
            }

            var replRej = message as ReplaceRejectMessage;
            if (replRej != null)
            {
                ApplyReplaceReject(order, replRej, message.SequenceNumber);
                return;
            }
        }

        private void ApplyFill(TrackedOrder order, FillMessage fill, long sequenceNumber)
        {
            if (order.State == OrderState.Rejected || order.State == OrderState.Cancelled || order.State == OrderState.Filled)
            {
                if (order.State == OrderState.Filled && fill.CumQty <= order.CumQty)
                {
                    order.AppliedMessageSequences.Add(sequenceNumber);
                    order.ProcessedExecIds.Add(fill.ExecId);
                    return;
                }

                if (order.State != OrderState.Filled)
                {
                    order.AppliedMessageSequences.Add(sequenceNumber);
                    return;
                }
            }

            order.CumQty = Math.Max(order.CumQty, fill.CumQty);
            order.ProcessedExecIds.Add(fill.ExecId);
            order.AppliedMessageSequences.Add(sequenceNumber);

            if (order.CumQty >= order.OrderQty && order.OrderQty > 0)
            {
                order.State = OrderState.Filled;
                ClearPendingMarkers(order);
                return;
            }

            if (order.State == OrderState.PendingCancel)
            {
                order.PendingAckSince = null;
            }
            else if (order.State == OrderState.PendingReplace)
            {
                order.PendingAckSince = null;
            }
            else
            {
                AssignOpenOrPartial(order);
                order.PendingAckSince = null;
            }
        }

        private void ApplyNewOrderAck(TrackedOrder order, long sequenceNumber)
        {
            order.AppliedMessageSequences.Add(sequenceNumber);

            if (order.State == OrderState.Rejected || order.State == OrderState.Filled || order.State == OrderState.Cancelled)
            {
                return;
            }

            if (order.State == OrderState.PendingAck || order.State == OrderState.StatusUnknown)
            {
                if (order.CumQty >= order.OrderQty && order.OrderQty > 0)
                {
                    order.State = OrderState.Filled;
                }
                else
                {
                    AssignOpenOrPartial(order);
                }

                order.PendingAckSince = null;
                return;
            }

            if (order.State == OrderState.Open || order.State == OrderState.PartiallyFilled || order.State == OrderState.PendingCancel)
            {
                order.PendingAckSince = null;
            }
        }

        private void ApplyNewOrderReject(TrackedOrder order, NewOrderRejectMessage rej, long sequenceNumber)
        {
            order.AppliedMessageSequences.Add(sequenceNumber);

            if (order.State == OrderState.Filled)
            {
                return;
            }

            order.LastRejectReason = rej.Reason;
            order.State = OrderState.Rejected;
            ClearPendingMarkers(order);
        }

        private void ApplyCancelAck(TrackedOrder order, long sequenceNumber)
        {
            order.AppliedMessageSequences.Add(sequenceNumber);

            if (order.State == OrderState.Filled)
            {
                return;
            }

            if (order.State == OrderState.PendingCancel || order.State == OrderState.StatusUnknown || order.State == OrderState.Open
                || order.State == OrderState.PartiallyFilled)
            {
                if (order.CumQty >= order.OrderQty && order.OrderQty > 0)
                {
                    order.State = OrderState.Filled;
                }
                else
                {
                    order.State = OrderState.Cancelled;
                }
            }

            ClearPendingMarkers(order);
        }

        private void ApplyCancelReject(TrackedOrder order, CancelRejectMessage rej, long sequenceNumber)
        {
            order.AppliedMessageSequences.Add(sequenceNumber);
            order.LastRejectReason = rej.Reason;

            if (order.State == OrderState.Filled || order.State == OrderState.Cancelled)
            {
                return;
            }

            if (order.State == OrderState.PendingCancel || order.State == OrderState.StatusUnknown)
            {
                if (order.CumQty >= order.OrderQty && order.OrderQty > 0)
                {
                    order.State = OrderState.Filled;
                }
                else
                {
                    AssignOpenOrPartial(order);
                }
            }

            ClearPendingMarkers(order);
        }

        private void ApplyReplaceAck(TrackedOrder order, ReplaceAckMessage ack, long sequenceNumber)
        {
            order.AppliedMessageSequences.Add(sequenceNumber);

            if (order.State != OrderState.PendingReplace && order.State != OrderState.StatusUnknown)
            {
                return;
            }

            order.OrderQty = ack.NewOrderQty;
            order.PendingReplaceSince = null;
            order.PendingReplaceTargetQty = null;

            if (order.CumQty >= order.OrderQty && order.OrderQty > 0)
            {
                order.State = OrderState.Filled;
                ClearPendingMarkers(order);
                return;
            }

            AssignOpenOrPartial(order);
            ClearPendingMarkers(order);
        }

        private void ApplyReplaceReject(TrackedOrder order, ReplaceRejectMessage rej, long sequenceNumber)
        {
            order.AppliedMessageSequences.Add(sequenceNumber);

            if (order.State != OrderState.PendingReplace && order.State != OrderState.StatusUnknown)
            {
                return;
            }

            order.LastRejectReason = rej.Reason;
            order.PendingReplaceSince = null;
            order.PendingReplaceTargetQty = null;
            AssignOpenOrPartial(order);
            ClearPendingMarkers(order);
        }

        private void ApplyExchangeCancelled(TrackedOrder order, ExchangeCancelledMessage msg, long sequenceNumber)
        {
            order.AppliedMessageSequences.Add(sequenceNumber);
            order.LastRejectReason = msg.Reason;

            if (order.State == OrderState.Filled)
            {
                return;
            }

            order.State = OrderState.Cancelled;
            ClearPendingMarkers(order);
        }

        private static void ClearPendingMarkers(TrackedOrder order)
        {
            order.PendingAckSince = null;
            order.PendingCancelSince = null;
            order.PendingReplaceSince = null;
            order.PendingReplaceTargetQty = null;
        }

        private static void AssignOpenOrPartial(TrackedOrder order)
        {
            if (order.OrderQty > 0 && order.CumQty >= order.OrderQty)
            {
                order.State = OrderState.Filled;
                return;
            }

            if (order.CumQty > 0)
            {
                order.State = OrderState.PartiallyFilled;
            }
            else
            {
                order.State = OrderState.Open;
            }
        }

        private static void EnsureState(TrackedOrder order, OrderState expected)
        {
            if (order.State != expected)
            {
                throw new InvalidOperationException(string.Format("Expected state {0}, was {1}.", expected, order.State));
            }
        }

        private TrackedOrder GetOrderOrThrow(string clOrdId)
        {
            TrackedOrder order;
            if (!_orders.TryGetValue(clOrdId, out order))
            {
                throw new KeyNotFoundException("Unknown ClOrdID: " + clOrdId);
            }

            return order;
        }

        private string NextClOrdId()
        {
            var id = Interlocked.Increment(ref _clOrdIdCounter);
            return "C" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void RaiseChanged()
        {
            var h = OrderChanged;
            if (h != null)
            {
                h(this, EventArgs.Empty);
            }
        }
    }
}
