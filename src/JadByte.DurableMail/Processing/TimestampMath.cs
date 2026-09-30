namespace JadByte.DurableMail.Processing;

internal static class TimestampMath
{
    public static DateTimeOffset AddDelay(DateTimeOffset now, TimeSpan delay) =>
        delay.Ticks >= DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks
            ? DateTimeOffset.MaxValue
            : now.ToUniversalTime() + delay;
}
