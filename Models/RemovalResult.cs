namespace EmbyStreams.Models
{
    /// <summary>
    /// Result of a removal operation.
    /// </summary>
    public record RemovalResult(
        bool IsSuccess,
        string Message
    )
    {
        public static RemovalResult Success(string message) => new(true, message);
        public static RemovalResult Failure(string message) => new(false, message);
    }
}
