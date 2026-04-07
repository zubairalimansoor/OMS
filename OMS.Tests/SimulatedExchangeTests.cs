using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OMS.Tests
{
    internal sealed class OmsInboundBridge : IInboundExchangeSink
    {
        public OmsEngine Engine { get; set; }

        public void OnMessageReceived(ExchangeMessage message)
        {
            Engine.OnMessageReceived(message);
        }
    }

    [TestFixture]
    public sealed class SimulatedExchangeTests
    {
        [Test]
        public async Task Simulator_PartialSlices_AlignWithOms()
        {
            var bridge = new OmsInboundBridge();
            var ex = new SimulatedExchange(bridge, new Random(42), TimeSpan.Zero)
            {
                EnableAutomaticFill = false
            };
            var oms = new OmsEngine(ex);
            bridge.Engine = oms;

            var id = await oms.SubmitNewOrderAsync(25).ConfigureAwait(false);
            await Task.Delay(80).ConfigureAwait(false);
            await ex.PublishFillSliceAsync(id, 10, 0).ConfigureAwait(false);
            await ex.PublishFillSliceAsync(id, 10, 0).ConfigureAwait(false);
            await ex.PublishFillSliceAsync(id, 5, 0).ConfigureAwait(false);

            var o = oms.TryGetOrder(id);
            var snap = ex.QueryExchange(id);
            Assert.That(o.State, Is.EqualTo(OrderState.Filled));
            Assert.That(snap.CumQty, Is.EqualTo(25));
            Assert.That(snap.ExchangeState, Is.EqualTo(OrderState.Filled));
        }

        [Test]
        public async Task Simulator_ReplaceSameClOrdId_UpdatesBook()
        {
            var bridge = new OmsInboundBridge();
            var ex = new SimulatedExchange(bridge, new Random(43), TimeSpan.Zero)
            {
                EnableAutomaticFill = false
            };
            var oms = new OmsEngine(ex);
            bridge.Engine = oms;

            var id = await oms.SubmitNewOrderAsync(100).ConfigureAwait(false);
            await Task.Delay(60).ConfigureAwait(false);
            await oms.SubmitReplaceAsync(id, 55).ConfigureAwait(false);
            await Task.Delay(80).ConfigureAwait(false);

            var o = oms.TryGetOrder(id);
            var snap = ex.QueryExchange(id);
            Assert.That(o.OrderQty, Is.EqualTo(55));
            Assert.That(snap.OrderQty, Is.EqualTo(55));
        }

        [Test]
        [NonParallelizable]
        public async Task Simulator_LostAck_Watchdog_Reconciles()
        {
            var bridge = new OmsInboundBridge();
            var ex = new SimulatedExchange(bridge, new Random(44), TimeSpan.FromMilliseconds(5))
            {
                ForceDropNextNewOrderAck = true,
                ForceDropNextFill = true,
                EnableAutomaticFill = true,
                FillExtraDelayMilliseconds = 0
            };
            var oms = new OmsEngine(ex);
            bridge.Engine = oms;

            var id = await oms.SubmitNewOrderAsync(100).ConfigureAwait(false);
            Thread.Sleep(900);

            var o = oms.TryGetOrder(id);
            var snap = ex.QueryExchange(id);
            Assert.That(o.CumQty, Is.EqualTo(snap.CumQty));
            Assert.That(snap.ExchangeState, Is.EqualTo(OrderState.Open));
        }
    }
}
