namespace Consumer.Beta;

public static class BetaDecision
{
    public static int Normalize(int value) => value switch
    {
        < 0 => 0,
        > 10 => 10,
        _ => value,
    };
}
