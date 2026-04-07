using NUnit.Framework;

namespace OMS.Tests
{
    [TestFixture]
    public sealed class ReconciliationMergeTests
    {
        [Test]
        public void Merge_HigherCumFromExchange_IncreasesCum()
        {
            var o = new TrackedOrder("C1", 100) { State = OrderState.StatusUnknown, CumQty = 0 };
            var snap = new ExchangeTruthSnapshot("C1", true, OrderState.PartiallyFilled, 100, 30, 0);
            ReconciliationMerge.Merge(o, snap);
            Assert.That(o.CumQty, Is.EqualTo(30));
            Assert.That(o.State, Is.EqualTo(OrderState.PartiallyFilled));
        }

        [Test]
        public void Merge_OrderMissing_AndUnknown_GoesRejected()
        {
            var o = new TrackedOrder("C1", 100) { State = OrderState.StatusUnknown };
            var snap = new ExchangeTruthSnapshot("C1", false, OrderState.Rejected, 0, 0, 0);
            ReconciliationMerge.Merge(o, snap);
            Assert.That(o.State, Is.EqualTo(OrderState.Rejected));
        }

        [Test]
        public void Merge_FullFill_Terminates()
        {
            var o = new TrackedOrder("C1", 100)
            {
                State = OrderState.StatusUnknown,
                CumQty = 100,
                OrderQty = 100
            };
            var snap = new ExchangeTruthSnapshot("C1", true, OrderState.Filled, 100, 100, 0);
            ReconciliationMerge.Merge(o, snap);
            Assert.That(o.State, Is.EqualTo(OrderState.Filled));
        }
    }
}
