using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;

namespace OpenHand.Patches;

internal static class HotbarCenteringTranspiler
{
    // Installed VS 1.22.7 OnRenderGUI: the initial shouldRecompose branch
    // joins at IL_0015, before renderGear; ISkillItemRenderer.Render is at
    // IL_0273. Match semantic operands, never IL offsets or local numbers.
    internal static List<CodeInstruction> Rewrite(
        List<CodeInstruction> original, MethodInfo prepare, MethodInfo renderSkill, out bool supported)
    {
        supported = false;
        int existingPrepare = original.FindIndex(c => c.opcode == OpCodes.Call && Equals(c.operand, prepare));
        int existingSkill = original.FindIndex(c => c.opcode == OpCodes.Call && Equals(c.operand, renderSkill));
        if (existingPrepare >= 0 || existingSkill >= 0)
        {
            // A duplicate registration must not wrap the renderer twice.
            supported = existingPrepare > 0 && existingSkill > existingPrepare &&
                        original.Count(c => Equals(c.operand, prepare)) == 1 &&
                        original.Count(c => Equals(c.operand, renderSkill)) == 1 &&
                        original[existingPrepare - 1].opcode == OpCodes.Ldarg_0 &&
                        original[existingSkill - 1].opcode == OpCodes.Ldarg_0 &&
                        !original.Any(c => Equals(c.operand, typeof(ISkillItemRenderer).GetMethod(nameof(ISkillItemRenderer.Render))));
            return original;
        }
        int hook = -1;
        int skill = -1;
        int hookCount = 0;
        int skillCount = 0;
        for (int i = 0; i < original.Count; i++)
        {
            if (original[i].opcode == OpCodes.Callvirt &&
                Equals(original[i].operand, typeof(ISkillItemRenderer).GetMethod(nameof(ISkillItemRenderer.Render))))
            {
                skill = i;
                skillCount++;
            }
            if (i + 9 >= original.Count) continue;
            if (original[i].opcode == OpCodes.Ldarg_0 &&
                IsField(original[i + 1], OpCodes.Ldfld, "shouldRecompose") &&
                (original[i + 2].opcode == OpCodes.Brfalse || original[i + 2].opcode == OpCodes.Brfalse_S) &&
                original[i + 2].operand is Label target &&
                original[i + 3].opcode == OpCodes.Ldarg_0 &&
                original[i + 4].opcode == OpCodes.Call &&
                original[i + 4].operand is MethodInfo method &&
                method.DeclaringType?.FullName == "Vintagestory.Client.NoObf.HudHotbar" &&
                method.Name == "ComposeGuis" && method.GetParameters().Length == 0 &&
                original[i + 5].opcode == OpCodes.Ldarg_0 &&
                original[i + 6].opcode == OpCodes.Ldc_I4_0 &&
                IsField(original[i + 7], OpCodes.Stfld, "shouldRecompose") &&
                original[i + 8].opcode == OpCodes.Ldarg_0 &&
                original[i + 8].labels.Contains(target) &&
                IsField(original[i + 9], OpCodes.Ldfld, "temporalStabilityEnabled"))
            {
                hook = i + 8;
                hookCount++;
            }
        }

        if (hookCount != 1 || skillCount != 1 || skill <= hook ||
            original.Take(hook + 1).Any(c => c.blocks.Count != 0) ||
            original[skill].blocks.Count != 0)
            return original;

        // Clone before moving labels so failure never partially mutates the
        // instruction stream supplied by Harmony or an earlier transpiler.
        List<CodeInstruction> result = new(original.Count + 3);
        for (int i = 0; i < original.Count; i++)
        {
            CodeInstruction instruction = new(original[i]);
            if (i == hook || i == skill)
            {
                CodeInstruction loadInstance = new(OpCodes.Ldarg_0);
                loadInstance.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                result.Add(loadInstance);
                if (i == hook) result.Add(new CodeInstruction(OpCodes.Call, prepare));
                else
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = renderSkill;
                }
            }
            result.Add(instruction);
        }
        supported = true;
        return result;
    }

    private static bool IsField(CodeInstruction instruction, OpCode opcode, string name) =>
        instruction.opcode == opcode && instruction.operand is FieldInfo field &&
        field.DeclaringType?.FullName == "Vintagestory.Client.NoObf.HudHotbar" &&
        field.FieldType == typeof(bool) && field.Name == name;
}
