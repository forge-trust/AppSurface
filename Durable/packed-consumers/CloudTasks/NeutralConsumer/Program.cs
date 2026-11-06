using ForgeTrust.AppSurface.Durable;

// A neutral packed consumer intentionally references only the base Durable package.
Console.WriteLine(typeof(DurableProblem).Assembly.GetName().Name);
