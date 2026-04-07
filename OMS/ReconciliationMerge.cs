using System;

namespace OMS
{
    /// <summary>Non-destructive merge of exchange truth into OMS after StatusUnknown / query.</summary>
    public static class ReconciliationMerge
    {
        public static void Merge(TrackedOrder order, ExchangeTruthSnapshot snap)
        {
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            if (snap == null)
            {
                throw new ArgumentNullException(nameof(snap));
            }

            if (!snap.OrderExists)
            {
                if (order.State == OrderState.StatusUnknown)
                {
                    order.State = OrderState.Rejected;
                    order.LastRejectReason = "QueryExchange: order not found at exchange.";
                }

                return;
            }

            if (snap.OrderQty > 0)
            {
                order.OrderQty = snap.OrderQty;
            }

            if (snap.CumQty > order.CumQty)
            {
                order.CumQty = snap.CumQty;
            }

            if (order.CumQty >= order.OrderQty && order.OrderQty > 0)
            {
                order.State = OrderState.Filled;
                ClearPending(order);
                return;
            }

            switch (snap.ExchangeState)
            {
                case OrderState.Filled:
                    if (order.CumQty >= order.OrderQty)
                    {
                        order.State = OrderState.Filled;
                    }

                    break;

                case OrderState.Cancelled:
                    if (order.State != OrderState.Filled)
                    {
                        order.State = OrderState.Cancelled;
                    }

                    break;

                case OrderState.Rejected:
                    order.State = OrderState.Rejected;
                    order.LastRejectReason = "Exchange reports Rejected (reconciliation).";
                    break;

                case OrderState.Open:
                case OrderState.PartiallyFilled:
                case OrderState.PendingAck:
                case OrderState.PendingCancel:
                case OrderState.PendingReplace:
                    if (order.State == OrderState.StatusUnknown)
                    {
                        order.State = snap.ExchangeState;
                    }

                    break;

                default:
                    if (order.State == OrderState.StatusUnknown)
                    {
                        order.State = snap.ExchangeState;
                    }

                    break;
            }

            if (order.IsTerminal())
            {
                ClearPending(order);
            }
        }

        private static void ClearPending(TrackedOrder order)
        {
            order.PendingAckSince = null;
            order.PendingCancelSince = null;
            order.PendingReplaceSince = null;
            order.PendingReplaceTargetQty = null;
        }
    }
}
