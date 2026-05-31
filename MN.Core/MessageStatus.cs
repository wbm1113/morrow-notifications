namespace MN.Core;

public enum MessageStatus
{
    Processing,
    FanOutComplete,
    Dispatched,
    PartiallyDispatched,
    DeliveryFailed,
    DeadLettered
}
