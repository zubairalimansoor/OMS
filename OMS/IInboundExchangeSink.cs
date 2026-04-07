using System;

namespace OMS
{
    /// <summary>Lets the transport wire asynchronous inbound messages into the OMS.</summary>
    public interface IInboundExchangeSink
    {
        void OnMessageReceived(ExchangeMessage message);
    }
}
