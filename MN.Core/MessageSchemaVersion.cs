namespace MN.Core;

public static class MessageSchemaVersion
{
    /// <summary>
    /// Current internal message schema version. Bump this when the NotificationMessage
    /// structure changes in a breaking way so old records can be identified and migrated.
    /// </summary>
    public const int Current = 1;
}
