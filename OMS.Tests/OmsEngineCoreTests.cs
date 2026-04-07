using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OMS.Tests
{
    [TestFixture]
    public sealed class OmsEngineCoreTests
    {
        [Test]
        public void SubmitNewOrder_StartsInPendingAck()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            var o = oms.TryGetOrder(id);
            Assert.That(o, Is.Not.Null);
            Assert.That(o.State, Is.EqualTo(OrderState.PendingAck));
            Assert.That(o.OrderQty, Is.EqualTo(100));
            Assert.That(o.CumQty, Is.EqualTo(0));
        }

        [Test]
        public void NewOrderAck_OpensOrder()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            Assert.That(oms.TryGetOrder(id).State, Is.EqualTo(OrderState.Open));
        }

        [Test]
        public void PartialFill_MovesToPartiallyFilled()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(25).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 10, 10, false));
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.PartiallyFilled));
            Assert.That(o.CumQty, Is.EqualTo(10));
        }

        [Test]
        public void MultiPartial_ThenFilled()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(25).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 10, 10, false));
            oms.OnMessageReceived(new FillMessage(3, id, "E2", 15, 25, true));
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.Filled));
            Assert.That(o.CumQty, Is.EqualTo(25));
        }

        [Test]
        public void DuplicateSequenceNumber_Ignored()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(10).GetAwaiter().GetResult();
            oms.OnMessageReceived(new FillMessage(1, id, "E1", 5, 5, false));
            oms.OnMessageReceived(new FillMessage(1, id, "E2", 99, 99, false));
            Assert.That(oms.TryGetOrder(id).CumQty, Is.EqualTo(5));
        }

        [Test]
        public void DuplicateExecId_DoesNotDoubleCount()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(20).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 5, 5, false));
            oms.OnMessageReceived(new FillMessage(3, id, "E1", 5, 5, false));
            Assert.That(oms.TryGetOrder(id).CumQty, Is.EqualTo(5));
        }

        [Test]
        public void FillBeforeAck_StillReachesPartiallyFilled()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(30).GetAwaiter().GetResult();
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 10, 10, false));
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.PartiallyFilled));
            Assert.That(o.CumQty, Is.EqualTo(10));
        }

        [Test]
        public void LateAck_AfterPartial_DoesNotResetQty()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(30).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 10, 10, false));
            oms.OnMessageReceived(new NewOrderAckMessage(3, id));
            var o = oms.TryGetOrder(id);
            Assert.That(o.CumQty, Is.EqualTo(10));
            Assert.That(o.State, Is.EqualTo(OrderState.PartiallyFilled));
        }

        [Test]
        public void Reject_ThenLateAck_DoesNotOpen()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(10).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderRejectMessage(1, id, "bad"));
            oms.OnMessageReceived(new NewOrderAckMessage(2, id));
            Assert.That(oms.TryGetOrder(id).State, Is.EqualTo(OrderState.Rejected));
        }

        [Test]
        public void CancelFlow_AckLeavesCancelled()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(40).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.SubmitCancelAsync(id).GetAwaiter().GetResult();
            Assert.That(oms.TryGetOrder(id).State, Is.EqualTo(OrderState.PendingCancel));
            oms.OnMessageReceived(new CancelAckMessage(2, id));
            Assert.That(oms.TryGetOrder(id).State, Is.EqualTo(OrderState.Cancelled));
        }

        [Test]
        public void CancelReject_OnPartial_ReturnsToPartiallyFilled()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(40).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 10, 10, false));
            oms.SubmitCancelAsync(id).GetAwaiter().GetResult();
            oms.OnMessageReceived(new CancelRejectMessage(3, id, "no"));
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.PartiallyFilled));
            Assert.That(o.CumQty, Is.EqualTo(10));
        }

        [Test]
        public void Replace_AckUpdatesQtyAndLeavesOpen()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.SubmitReplaceAsync(id, 55).GetAwaiter().GetResult();
            Assert.That(oms.TryGetOrder(id).State, Is.EqualTo(OrderState.PendingReplace));
            oms.OnMessageReceived(new ReplaceAckMessage(2, id, 55));
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.Open));
            Assert.That(o.OrderQty, Is.EqualTo(55));
            Assert.That(o.CumQty, Is.EqualTo(0));
        }

        [Test]
        public void Replace_WhenCumQty_PartiallyFilled_AfterAck()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 30, 30, false));
            oms.SubmitReplaceAsync(id, 80).GetAwaiter().GetResult();
            oms.OnMessageReceived(new ReplaceAckMessage(3, id, 80));
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.PartiallyFilled));
            Assert.That(o.OrderQty, Is.EqualTo(80));
            Assert.That(o.CumQty, Is.EqualTo(30));
        }

        [Test]
        public void Replace_BelowFilledQty_Throws()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 30, 30, false));
            Assert.Throws<InvalidOperationException>(() => oms.SubmitReplaceAsync(id, 20).GetAwaiter().GetResult());
        }

        [Test]
        public void ReplaceReject_RevertsToPartiallyFilled()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(50).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.OnMessageReceived(new FillMessage(2, id, "E1", 10, 10, false));
            oms.SubmitReplaceAsync(id, 40).GetAwaiter().GetResult();
            oms.OnMessageReceived(new ReplaceRejectMessage(3, id, "nope"));
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.PartiallyFilled));
            Assert.That(o.OrderQty, Is.EqualTo(50));
        }

        [Test]
        public void PendingReplace_BlocksSecondReplace()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.SubmitReplaceAsync(id, 70).GetAwaiter().GetResult();
            oms.SubmitReplaceAsync(id, 60).GetAwaiter().GetResult();
            var replaceCalls = 0;
            foreach (var c in g.Calls)
            {
                if (c.Item1 == "Replace")
                {
                    replaceCalls++;
                }
            }

            Assert.That(replaceCalls, Is.EqualTo(1));
        }

        [Test]
        public void PendingReplace_BlocksCancel()
        {
            var g = new FakeRecordingGateway();
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            oms.OnMessageReceived(new NewOrderAckMessage(1, id));
            oms.SubmitReplaceAsync(id, 55).GetAwaiter().GetResult();
            oms.SubmitCancelAsync(id).GetAwaiter().GetResult();
            var cancelCalls = 0;
            foreach (var c in g.Calls)
            {
                if (c.Item1 == "Cancel")
                {
                    cancelCalls++;
                }
            }

            Assert.That(cancelCalls, Is.EqualTo(0));
            Assert.That(oms.TryGetOrder(id).State, Is.EqualTo(OrderState.PendingReplace));
        }

        [Test]
        [NonParallelizable]
        public void Watchdog_StuckPendingAck_ReconcilesToOpen()
        {
            var g = new FakeRecordingGateway();
            g.QueryHandler = clOrdId => new ExchangeTruthSnapshot(clOrdId, true, OrderState.Open, 100, 0, 0);
            var oms = new OmsEngine(g);
            var id = oms.SubmitNewOrderAsync(100).GetAwaiter().GetResult();
            Thread.Sleep(900);
            var o = oms.TryGetOrder(id);
            Assert.That(o.State, Is.EqualTo(OrderState.Open));
            Assert.That(o.CumQty, Is.EqualTo(0));
        }
    }
}
