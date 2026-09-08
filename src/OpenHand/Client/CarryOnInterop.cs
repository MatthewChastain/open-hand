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
//
// Two CarryOn layouts are supported, tried in order and then cached:
// - 2.0.0: carry state lives behind ICarryManager.GetCarried(Entity, slot),
//   reached through the CarryOnLib library mod's ModSystem instance
//   (CarryOnLibSystem.CarryManager). Decompile evidence: CarryOnLib
//   1.0.0-pre.8 (CarryOnLibSystem exposes the manager; ICarryManager declares
//   GetCarried) and CarryOn 2.0.0-pre.8 (CarrySystem.Start wires the
//   implementation; CarryManager.GetCarried delegates to CarryStateService,
//   which returns null when nothing is carried, and the hands slot is
//   CarrySlot.Hands).
// - 1.14.x: static CarryableExtensions.GetCarried(Entity, slot-enum)
//   extension, resolved by AccessTools.TypeByName.
// A missing or renamed API under either layout simply reports "not carrying".
internal static class CarryOnInterop
{
    private const string LibSystemName = "CarryOn.CarryOnLib.CarryOnLibSystem";

    // 2.0.0 layout state: the library ModSystem instance is stable for the
    // process, but its CarryManager property is wired by CarryOn's own Start
    // and may run after ours — so the property is re-read per call. Calls are
    // event-driven (never per tick), so the reflection invoke cost is
    // irrelevant.
    private static object? libSystem;
    private static PropertyInfo? carryManagerProperty;
    private static MethodInfo? instanceGetCarried;

    // 1.14.x layout state: static extension method plus its slot argument.
    private static MethodInfo? getCarriedExtension;
    private static object? handsSlot;

    private static bool resolved;

    internal static bool IsCarryingHands(Entity entity)
    {
        if (!Resolve())
        {
            return false;
        }

        try
        {
            if (instanceGetCarried is not null)
            {
                // A null manager just means CarryOn has not finished wiring
                // itself yet; report not-carrying until it has.
                object? manager = carryManagerProperty!.GetValue(libSystem);
                if (manager is null)
                {
                    return false;
                }

                return instanceGetCarried.Invoke(manager, [entity, handsSlot!]) is not null;
            }

            return getCarriedExtension!.Invoke(null, [entity, handsSlot!]) is not null;
        }
        catch
        {
            // A CarryOn update that changes the API's shape must never
            // break Open Hand's own input handling.
            return false;
        }
    }

    private static bool Resolve()
    {
        if (resolved)
        {
            return instanceGetCarried is not null || getCarriedExtension is not null;
        }

        resolved = true;

        if (TryResolveCarryManager())
        {
            return true;
        }

        return TryResolveExtension();
    }

    // CarryOn 2.0.0: CarryOnLibSystem (in the CarryOnLib library mod) exposes
    // an ICarryManager instance whose GetCarried(Entity, CarrySlot) reports
    // carried state. CarrySystem.Start assigns the manager
    // (CarryOnLibSystem.CarryManager = ...), so fetch the ModSystem instance
    // eagerly but tolerate the manager itself not being wired yet.
    private static bool TryResolveCarryManager()
    {
        Type? libSystemType = AccessTools.TypeByName(LibSystemName);
        if (libSystemType is null)
        {
            return false;
        }

        PropertyInfo? managerProperty = AccessTools.Property(libSystemType, "CarryManager");
        MethodInfo? getCarried = managerProperty is null
            ? null
            : FindGetCarried(managerProperty.PropertyType);
        if (managerProperty is null || getCarried is null)
        {
            return false;
        }

        object? system = OpenHandModSystem.ClientApi?.ModLoader.GetModSystem(LibSystemName);
        if (system is null)
        {
            return false;
        }

        libSystem = system;
        carryManagerProperty = managerProperty;
        instanceGetCarried = getCarried;
        return true;
    }

    // CarryOn 1.14.x: a static GetCarried(this Entity, slot-enum) extension on
    // CarryableExtensions. Resolve the slot argument from the method's own
    // enum type instead of hardcoding numeric values.
    private static bool TryResolveExtension()
    {
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
                getCarriedExtension = candidate;
                return true;
            }
        }

        return false;
    }

    // Shared by the 2.0.0 path: find GetCarried(Entity, slot-enum) on the
    // manager's interface and resolve the hands slot from that enum, mirroring
    // the extension resolver above.
    private static MethodInfo? FindGetCarried(Type managerType)
    {
        foreach (MethodInfo candidate in AccessTools.GetDeclaredMethods(managerType))
        {
            if (candidate.Name != "GetCarried")
            {
                continue;
            }

            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 2 &&
                typeof(Entity).IsAssignableFrom(parameters[0].ParameterType) &&
                parameters[1].ParameterType.IsEnum &&
                Enum.IsDefined(parameters[1].ParameterType, "Hands"))
            {
                handsSlot = Enum.Parse(parameters[1].ParameterType, "Hands");
                return candidate;
            }
        }

        return null;
    }
}
