namespace NativePreparation.Subject;

/// <summary>Small ordinary calculator library used by the native coverage fixture.</summary>
public static class Calculator
{
    /// <summary>Calculates with operation codes 0 (add), 1 (subtract), 2 (multiply), and 3 (divide).</summary>
    /// <exception cref="DivideByZeroException">Division is requested with a zero divisor.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The operation code is unsupported.</exception>
    public static int Calculate(int left, int right, int operation)
        => operation switch
        {
            0 => left + right,
            1 => left - right,
            2 => left * right,
            3 when right != 0 => left / right,
            3 => throw new DivideByZeroException("The divisor cannot be zero."),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported operation code."),
        };
}
