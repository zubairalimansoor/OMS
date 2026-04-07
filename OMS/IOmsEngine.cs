using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OMS
{
    public interface IOmsEngine
    {
        /// <summary>Raised after any order mutation (for tests / logging).</summary>
        event EventHandler OrderChanged;

        Task<string> SubmitNewOrderAsync(decimal quantity);
        Task SubmitCancelAsync(string clOrdId);

        /// <summary>Single replace command for an existing working order (same ClOrdID at the venue).</summary>
        Task SubmitReplaceAsync(string clOrdId, decimal newOrderQty);

        TrackedOrder TryGetOrder(string clOrdId);
        IReadOnlyDictionary<string, TrackedOrder> SnapshotOrders();
    }
}
