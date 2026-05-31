namespace MN.BusinessLogic;

public sealed class RateLimitExceededException(string message) : Exception(message);
