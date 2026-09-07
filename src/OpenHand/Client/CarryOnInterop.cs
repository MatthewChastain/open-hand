using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace OpenHand.Client;

// Read-only interop with CarryOn: reports whether the local player currently
// carries a block in their hands, so Open Hand can lock the selection while
// carrying (CarryOn blocks slot changes while carrying anyway, and exiting
// Open Hand mid-carry strands the player on a slot that cannot place the
// block). Everything is resolved by reflection — CarryOn stays an optional
// dependency; a missing or renamed API simply reports "not carrying".
internal static class CarryOnInterop
{
    private static MethodInfo? getCarried;
    private static object? handsSlot;
    private static bool resolved;

    // Resolves once and caches; calls are event-driven (never per tick), so
    // the reflection invoke cost is irrelevant.
    internal static bool IsCarryingHands(Entity entity)
    {
        if (!Resolve())
        {
            return false;
        }

        try
        {
            return getCarried!.Invoke(null, new[] { (object)entity, handsSlot! }) is not null;
        }
        catch
        {
            // A CarryOn update that changes the extension's shape must never
            // break Open Hand's own input handling.
            return false;
        }
    }

    private static bool Resolve()
    {
        if (resolved)
        {
            return getCarried is not null;
        }

        resolved = true;
        Type? extensions = AccessTools.TypeByName("CarryOn.API.Common.CarryableExtensions");
        if (extensions is null)
        {
            return false;
        }

        foreach (MethodInfo candidate in AccessTools.GetDeclaredMethods(extensions))
        {
            if (candidate.Name != "GetCarried")
            {
                continue;
            }

            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 2 &&
                typeof(Entity).IsAssignableFrom(parameters[0].ParameterType) &&
                parameters[1].ParameterType.IsEnum)
            {
                // Resolve the slot argument from the method's own enum type
                // instead of hardcoding numeric values.
                handsSlot = Enum.Parse(parameters[1].ParameterType, "Hands");
                getCarried = candidate;
                return true;
            }
        }

        return false;
    }
}
