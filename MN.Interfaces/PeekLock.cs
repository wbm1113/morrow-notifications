namespace MN.Interfaces;

/// <summary>
/// A peek-locked message: held in-flight until completed (success) or abandoned (retry).
/// Models Azure Service Bus peek-lock semantics.
/// </summary>
public record PeekLock<TMessage>(Guid LockToken, TMessage Message) where TMessage : class;
