using System.Reflection;
using OpenHand.Client;
using Vintagestory.API.Common;

internal static class CarryOnInteropTests
{
    internal static void Run()
    {
        try
        {
            // Without CarryOn (or before resolution), the interop must report
            // not-carrying and never throw — Open Hand's own input handling
            // depends on that guarantee.
            ResetInterop();
            EntityPlayer entity = (EntityPlayer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            bool carryingWithoutMod = CarryOnInterop.IsCarryingHands(entity);
            TestFakes.Require(!carryingWithoutMod, "without CarryOn the interop reports not-carrying");
            string unresolvedDescription = CarryOnInterop.Describe();
            TestFakes.Require(unresolvedDescription.Contains("CarryOn"), "Describe always names the interop state");

            Assembly[]? loaded = LoadCarryOnAssemblies();
            if (loaded is null)
            {
                Console.WriteLine("CarryOn is not installed in any known mod folder; interop resolution checks skipped.");
                return;
            }

            // With the real assemblies loaded, resolution must succeed through
            // whichever layout is installed (2.0 needs AnyApi, which is null in
            // the test host, so both the resolved and degraded paths must end
            // at a safe "not carrying" for an uninitialized entity).
            bool carrying = CarryOnInterop.IsCarryingHands(entity);
            TestFakes.Require(!carrying, "a fresh uninitialized entity reports not-carrying with CarryOn loaded");
            // The 2.x manager path needs the game's mod loader (AnyApi), which
            // the test host does not have, so a degraded description is
            // expected here — the invariant is that Describe always reports a
            // state and never throws.
            string resolvedDescription = CarryOnInterop.Describe();
            TestFakes.Require(!string.IsNullOrWhiteSpace(resolvedDescription),
                "Describe always reports the interop state");
            Console.WriteLine($"Passed CarryOn interop checks against the installed layout ({string.Join(", ", loaded.Select(a => a.GetName().Name))}).");
        }
        finally
        {
            ResetInterop();
        }
    }

    // Probe the known mod folders (the test instance's XDG data folder and the
    // main config folder). Mods ship as zips, so extract the newest CarryOn
    // and CarryOnLib zips into a scratch directory and load the extracted
    // assemblies. Nothing is asserted about their internals beyond "loads and
    // reports".
    private static Assembly[]? LoadCarryOnAssemblies()
    {
        string[] folders =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "code/vs-testing/xdg/VintagestoryData/Mods"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config/VintagestoryData/Mods")
        ];
        List<string> zips = [];
        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            // Newest last alphabetically in each family (CarryOn-1.22.0_v1.14.3
            // < CarryOn-1.22.0_v2.0.0-pre.8).
            string? carryOn = Directory.GetFiles(folder, "CarryOn*.zip").Order().LastOrDefault();
            string? carryOnLib = Directory.GetFiles(folder, "CarryOnLib*.zip").Order().LastOrDefault();
            if (carryOn is not null) zips.Add(carryOn);
            if (carryOnLib is not null) zips.Add(carryOnLib);
        }
        if (zips.Count == 0)
        {
            return null;
        }

        List<Assembly> loaded = [];
        string scratch = Path.Combine(Path.GetTempPath(), "openhand-tests", $"carryon-{Path.GetRandomFileName()}");
        foreach (string zip in zips)
        {
            string dir = Path.Combine(scratch, Path.GetFileNameWithoutExtension(zip));
            Directory.CreateDirectory(dir);
            // Mod zips sometimes carry duplicate entries; overwrite them.
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
            foreach (string dll in Directory.GetFiles(dir, "*.dll"))
            {
                try
                {
                    loaded.Add(Assembly.LoadFrom(dll));
                }
                catch (Exception)
                {
                    // A partial install (missing dependencies, wrong game
                    // version) must degrade the same way a missing mod does.
                }
            }
        }
        return loaded.Count > 0 ? [.. loaded] : null;
    }

    private static void ResetInterop()
    {
        foreach (string field in new[] { "getCarriedExtension", "libSystem", "carryManagerProperty", "instanceGetCarried", "handsSlot" })
        {
            typeof(CarryOnInterop).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
        typeof(CarryOnInterop).GetField("resolved", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
    }
}
