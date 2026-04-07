using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OMS.Tests
{
    internal sealed class FakeRecordingGateway : IExchangeGateway
    {
        public List<Tuple<string, object>> Calls { get; } = new List<Tuple<string, object>>();

        public Func<string, ExchangeTruthSnapshot> QueryHandler { get; set; }

        public FakeRecordingGateway()
        {
            QueryHandler = clOrdId => new ExchangeTruthSnapshot(clOrdId, true, OrderState.Open, 100, 0, 0);
        }

        public Task SubmitNewOrderAsync(string clOrdId, decimal quantity)
        {
            Calls.Add(Tuple.Create("New", (object)new { clOrdId, quantity }));
            return Task.CompletedTask;
        }

        public Task SubmitReplaceAsync(string clOrdId, decimal newOrderQty)
        {
            Calls.Add(Tuple.Create("Replace", (object)new { clOrdId, newOrderQty }));
            return Task.CompletedTask;
        }

        public Task SubmitCancelAsync(string clOrdId)
        {
            Calls.Add(Tuple.Create("Cancel", (object)clOrdId));
            return Task.CompletedTask;
        }

        public ExchangeTruthSnapshot QueryExchange(string clOrdId)
        {
            Calls.Add(Tuple.Create("Query", (object)clOrdId));
            return QueryHandler(clOrdId);
        }
    }
}
