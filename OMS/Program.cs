using System;
using System.Globalization;
using System.Threading.Tasks;

namespace OMS
{
    internal sealed class Program
    {
        private sealed class OmsInboundBridge : IInboundExchangeSink
        {
            public OmsEngine Engine { get; set; }

            public void OnMessageReceived(ExchangeMessage message)
            {
                var e = Engine;
                if (e == null)
                {
                    throw new InvalidOperationException("OMS engine not wired.");
                }

                e.OnMessageReceived(message);
            }
        }

        private static void Main()
        {
            Console.WriteLine("OMS (.NET Framework 4.8)");
            RunScenarioA();
            Console.WriteLine();
            RunScenarioB();
            Console.WriteLine();
            RunScenarioC();
            Console.WriteLine();
            RunScenarioDPartialFills();
            Console.WriteLine();
            RunScenarioECancelReject();
            Console.WriteLine();
            RunScenarioFReplace();
            Console.WriteLine();
            Console.WriteLine("Done.");
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey(intercept: true);
            }
        }

        private static void RunScenarioA()
        {
            Console.WriteLine("=== Scenario A: Standard fill ===");
            var rng = new Random(1001);
            var bridge = new OmsInboundBridge();
            var exchange = new SimulatedExchange(bridge, rng, TimeSpan.FromMilliseconds(20))
            {
                FillExtraDelayMilliseconds = 40,
                DropOutboundProbability = 0,
                UnilateralCancelProbability = 0
            };

            var oms = new OmsEngine(exchange);
            bridge.Engine = oms;

            RunAsync(async () =>
            {
                var id = await oms.SubmitNewOrderAsync(100).ConfigureAwait(false);
                await Task.Delay(1500).ConfigureAwait(false);
                PrintReconciliation("A", oms, exchange, id);
            });
        }

        private static void RunScenarioB()
        {
            Console.WriteLine("=== Scenario B: Lost ack / lost fill (watchdog + QueryExchange) ===");
            var rng = new Random(2002);
            var bridge = new OmsInboundBridge();
            var exchange = new SimulatedExchange(bridge, rng, TimeSpan.FromMilliseconds(15))
            {
                FillExtraDelayMilliseconds = 250,
                DropOutboundProbability = 0,
                UnilateralCancelProbability = 0
            };

            var oms = new OmsEngine(exchange);
            bridge.Engine = oms;

            RunAsync(async () =>
            {
                exchange.ForceDropNextNewOrderAck = true;
                exchange.ForceDropNextFill = true;
                var id = await oms.SubmitNewOrderAsync(100).ConfigureAwait(false);
                await Task.Delay(900).ConfigureAwait(false);
                PrintReconciliation("B", oms, exchange, id);
            });
        }

        private static void RunScenarioC()
        {
            Console.WriteLine("=== Scenario C: Cancel vs asynchronous fill (race) ===");
            var rng = new Random(3003);
            var bridge = new OmsInboundBridge();
            var exchange = new SimulatedExchange(bridge, rng, TimeSpan.FromMilliseconds(15))
            {
                FillExtraDelayMilliseconds = 120,
                DropOutboundProbability = 0,
                UnilateralCancelProbability = 0
            };

            var oms = new OmsEngine(exchange);
            bridge.Engine = oms;

            RunAsync(async () =>
            {
                var id = await oms.SubmitNewOrderAsync(100).ConfigureAwait(false);
                await Task.Delay(25).ConfigureAwait(false);
                await oms.SubmitCancelAsync(id).ConfigureAwait(false);
                await Task.Delay(1800).ConfigureAwait(false);
                PrintReconciliation("C", oms, exchange, id);
            });
        }

        private static void RunScenarioDPartialFills()
        {
            Console.WriteLine("=== Scenario D: Staged partial fills (10 + 10 + 5 of 25) ===");
            var rng = new Random(4004);
            var bridge = new OmsInboundBridge();
            var exchange = new SimulatedExchange(bridge, rng, TimeSpan.FromMilliseconds(12))
            {
                EnableAutomaticFill = false,
                DropOutboundProbability = 0,
                UnilateralCancelProbability = 0
            };

            var oms = new OmsEngine(exchange);
            bridge.Engine = oms;

            RunAsync(async () =>
            {
                var id = await oms.SubmitNewOrderAsync(25).ConfigureAwait(false);
                await Task.Delay(350).ConfigureAwait(false);
                await exchange.PublishFillSliceAsync(id, 10, 0).ConfigureAwait(false);
                await Task.Delay(80).ConfigureAwait(false);
                await exchange.PublishFillSliceAsync(id, 10, 0).ConfigureAwait(false);
                await Task.Delay(80).ConfigureAwait(false);
                await exchange.PublishFillSliceAsync(id, 5, 0).ConfigureAwait(false);
                await Task.Delay(400).ConfigureAwait(false);
                PrintReconciliation("D", oms, exchange, id);
            });
        }

        private static void RunScenarioECancelReject()
        {
            Console.WriteLine("=== Scenario E: Cancel reject (venue declines cancel; OMS returns to Open) ===");
            var rng = new Random(5005);
            var bridge = new OmsInboundBridge();
            var exchange = new SimulatedExchange(bridge, rng, TimeSpan.FromMilliseconds(12))
            {
                EnableAutomaticFill = false,
                DropOutboundProbability = 0,
                UnilateralCancelProbability = 0
            };

            var oms = new OmsEngine(exchange);
            bridge.Engine = oms;

            RunAsync(async () =>
            {
                var id = await oms.SubmitNewOrderAsync(40).ConfigureAwait(false);
                await Task.Delay(350).ConfigureAwait(false);
                exchange.ForceNextCancelReject = true;
                exchange.ForceNextCancelRejectReason = "Venue declined cancel (risk control) — simulated.";
                await oms.SubmitCancelAsync(id).ConfigureAwait(false);
                await Task.Delay(400).ConfigureAwait(false);
                PrintReconciliation("E", oms, exchange, id);
                var o = oms.TryGetOrder(id);
                if (o != null && o.LastRejectReason != null)
                {
                    Console.WriteLine("Last cancel reject reason: " + o.LastRejectReason);
                }
            });
        }

        private static void RunScenarioFReplace()
        {
            Console.WriteLine("=== Scenario F: Replace (single venue replace — same ClOrdID, new total qty) ===");
            var rng = new Random(6006);
            var bridge = new OmsInboundBridge();
            var exchange = new SimulatedExchange(bridge, rng, TimeSpan.FromMilliseconds(12))
            {
                EnableAutomaticFill = false,
                FillExtraDelayMilliseconds = 0,
                DropOutboundProbability = 0,
                UnilateralCancelProbability = 0
            };

            var oms = new OmsEngine(exchange);
            bridge.Engine = oms;

            RunAsync(async () =>
            {
                var id = await oms.SubmitNewOrderAsync(100).ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);
                await oms.SubmitReplaceAsync(id, 55).ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
                PrintReconciliation("F", oms, exchange, id);
            });
        }

        private static void RunAsync(Func<Task> body)
        {
            try
            {
                body().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Scenario failed: " + ex);
            }
        }

        private static void PrintReconciliation(string tag, OmsEngine oms, SimulatedExchange exchange, string clOrdId)
        {
            var order = oms.TryGetOrder(clOrdId);
            var snap = exchange.QueryExchange(clOrdId);

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Reconciliation table [{0}]", tag));
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-10}{1,-16}{2,-10}{3,-16}{4,-10}{5,-8}", "ClOrdID", "OMS State", "OMS Cum", "Exch State", "Exch Cum", "Match"));

            var omsState = order != null ? order.State.ToString() : "(missing)";
            var omsCum = order != null ? order.CumQty.ToString(CultureInfo.InvariantCulture) : "-";
            var exState = snap.OrderExists ? snap.ExchangeState.ToString() : "(none)";
            var exCum = snap.OrderExists ? snap.CumQty.ToString(CultureInfo.InvariantCulture) : "-";
            var ok = ReconciliationMatch(order, snap);

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-10}{1,-16}{2,-10}{3,-16}{4,-10}{5,-8}", clOrdId, omsState, omsCum, exState, exCum, ok ? "YES" : "NO"));
            Console.WriteLine("------------------------------------------------------------");
            if (!ok && order != null && snap.OrderExists)
            {
                Console.WriteLine("Note: mismatch detail — leaves OMS " + order.LeavesQty + ", exchange leaves " + snap.LeavesQty);
            }
        }

        private static bool ReconciliationMatch(TrackedOrder oms, ExchangeTruthSnapshot ex)
        {
            if (oms == null || ex == null || !ex.OrderExists)
            {
                return false;
            }

            if (oms.OrderQty != ex.OrderQty || oms.CumQty != ex.CumQty)
            {
                return false;
            }

            if (oms.State == OrderState.StatusUnknown || oms.State == OrderState.New)
            {
                return false;
            }

            if (oms.State == ex.ExchangeState)
            {
                return true;
            }

            if (oms.State == OrderState.PendingAck)
            {
                return ex.ExchangeState == OrderState.PendingAck || ex.ExchangeState == OrderState.Open;
            }

            if (oms.State == OrderState.PendingCancel)
            {
                return ex.ExchangeState == OrderState.Open
                    || ex.ExchangeState == OrderState.PartiallyFilled
                    || ex.ExchangeState == OrderState.Cancelled
                    || ex.ExchangeState == OrderState.Filled;
            }

            if (oms.State == OrderState.PartiallyFilled)
            {
                return ex.ExchangeState == OrderState.PartiallyFilled
                    || ex.ExchangeState == OrderState.Open;
            }

            if (oms.State == OrderState.PendingReplace)
            {
                return ex.ExchangeState == OrderState.Open
                    || ex.ExchangeState == OrderState.PartiallyFilled;
            }

            return false;
        }
    }
}
