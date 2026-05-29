using MN.Models;

namespace MN.Interfaces;

/// <summary>
/// A peek-locked message: the message is held in-flight and will not be
/// re-delivered until explicitly completed (success) or abandoned (retry).
/// Models Azure Service Bus peek-lock semantics.
/// </summary>
public record PeekLock(Guid LockToken, ProcessingMessage Message);
