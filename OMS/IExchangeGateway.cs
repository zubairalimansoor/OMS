using System;
using System.Threading.Tasks;

namespace OMS
{
    /// <summary>Abstraction for submitting commands and querying the exchange (DI-friendly).</summary>
    public interface IExchangeGateway
    {
        Task SubmitNewOrderAsync(string clOrdId, decimal quantity);

        /// <summary>Single replace/amend for an existing working order (venue-native semantics; in-proc simulator applies new total quantity).</summary>
        Task SubmitReplaceAsync(string clOrdId, decimal newOrderQty);

        Task SubmitCancelAsync(string clOrdId);
        ExchangeTruthSnapshot QueryExchange(string clOrdId);
    }
}
