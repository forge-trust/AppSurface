namespace Consumer.Alpha;

public static class AlphaDecision
{
    public static string Describe(int value) => value >= 0 ? "non-negative" : "negative";
}
