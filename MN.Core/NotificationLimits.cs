namespace MN.Core;

public static class NotificationLimits
{
    /// <summary>
    /// Maximum JSON payload size accepted at ingest and stored on message/outbox rows.
    /// Large enough for rich notification context; not a blob store.
    /// </summary>
    public const int MaxPayloadLength = 32_768;
}
