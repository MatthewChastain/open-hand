// The zero-framework console runner: each suite is a static class with a
// Run() method; one failing suite records its exception and the remaining
// suites still run (no early aborts). CI gates on a non-zero exit code.
List<string> passed = [];
Dictionary<string, Exception> failures = [];

void Run(string name, Action suite)
{
    try
    {
        suite();
        passed.Add(name);
    }
    catch (Exception failure)
    {
        failures[name] = failure;
    }
}

Run("selection state", SelectionStateTests.Run);
Run("wheel ring", WheelRingTests.Run);
Run("double tap", DoubleTapTests.Run);
Run("gap solver", GapSolverTests.Run);
Run("config", ConfigTests.Run);
Run("HUD geometry", HudGeometryTests.Run);
Run("centering geometry", CenteringGeometryTests.Run);
Run("CarryOn anchor solver", CarryAnchorSolverTests.Run);
Run("protocol", ProtocolTests.Run);

foreach (string name in passed)
{
    Console.WriteLine($"PASS {name}");
}
foreach ((string name, Exception failure) in failures)
{
    Console.Error.WriteLine($"FAIL {name}: {failure.Message}");
}
if (failures.Count > 0)
{
    Environment.Exit(1);
}
Console.WriteLine($"OpenHand state tests passed ({passed.Count} suites).");
