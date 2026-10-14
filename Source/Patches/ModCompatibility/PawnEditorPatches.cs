using static NonBinaryGender.Logger;
using HarmonyLib;
using PawnEditor;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;

namespace NonBinaryGender.Patches
{
    public static class PawnEditorPatches
    {
        public static void PatchPE(this Harmony harmony)
        {
            //The Pawn Editor fork moved the gender menus into their own methods: DrawSexButton(Pawn) on the bio tab
            //and GenderOptions() on the appearance editor. Same "Male" then List.Add pattern, so the same transpiler works
            MethodInfo drawSexButton = AccessTools.Method(typeof(TabWorker_Bio_Humanlike), "DrawSexButton");
            if (drawSexButton != null)
            {
                harmony.Patch(drawSexButton, transpiler: new HarmonyMethod(AccessTools.Method(typeof(PawnEditorPatches), nameof(DrawSexButtonTranspiler))));
            }
            else
            {
                harmony.Patch(AccessTools.Method(typeof(TabWorker_Bio_Humanlike), "DoButtons"), transpiler: new HarmonyMethod(AccessTools.Method(typeof(PawnEditorPatches), nameof(DoButtonsTranspiler))));
            }
            MethodInfo genderOptions = AccessTools.Method(typeof(Dialog_AppearanceEditor), "GenderOptions");
            harmony.Patch(genderOptions ?? AccessTools.Method(typeof(Dialog_AppearanceEditor), "DoLeftSection"), transpiler: new HarmonyMethod(AccessTools.Method(typeof(PawnEditorPatches), nameof(DoLeftSectionTranspiler))));
            harmony.Patch(AccessTools.Method(typeof(Dialog_AppearanceEditor), "IsAllowed", [typeof(HeadTypeDef), typeof(Pawn)]), transpiler: new HarmonyMethod(AccessTools.Method(typeof(PawnEditorPatches), nameof(HeadTypeTranspiler))));

            MethodInfo HeadTypePredicate = FindPredicate();
            if (HeadTypePredicate != null)
            {
                harmony.Patch(HeadTypePredicate, prefix: new HarmonyMethod(AccessTools.Method(typeof(PawnEditorPatches), nameof(HeadTypePredicatePrefix))));
            }
            else
            {
                //The Pawn Editor fork moved the head type filter into HeadTypes(), where it's a closure method
                MethodInfo closurePredicate = FindClosurePredicate();
                if (closurePredicate != null)
                {
                    harmony.Patch(closurePredicate, prefix: new HarmonyMethod(AccessTools.Method(typeof(PawnEditorPatches), nameof(HeadTypeClosurePredicatePrefix))));
                }
                else
                {
                    LogUtil.Warning("Could not find Pawn Editor's head type filter; enby pawns may not see all head types there");
                }
            }

            static MethodInfo FindClosurePredicate()
            {
                foreach (Type nested in typeof(Dialog_AppearanceEditor).GetNestedTypes(AccessTools.all).Where(t => t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null))
                {
                    if (AccessTools.Field(nested, "<>4__this") == null)
                    {
                        continue;
                    }
                    foreach (MethodInfo method in nested.GetMethods(AccessTools.all))
                    {
                        if (method.ReturnType == typeof(bool) && method.Name.Contains("HeadTypes") && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(HeadTypeDef) && method.GetParameters()[0].Name == "type")
                        {
                            return method;
                        }
                    }
                }
                return null;
            }

            static MethodInfo FindPredicate()
            {
                MethodInfo result = null;
                foreach (MethodInfo method in typeof(Dialog_AppearanceEditor).GetMethods(AccessTools.all).Where(t => t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null).ToList())
                {
                    if (method.ReturnType == typeof(bool) && method.Name.Contains("DoWindowContents") && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(HeadTypeDef) && method.GetParameters()[0].Name == "type")
                    {
                        if (result != null)
                        {
                            LogUtil.Error("Multiple matching methods found: " + result.Name + " and " + method.Name);
                        }
                        result = method;
                    }
                }
                return result;
            }
        }

        public static IEnumerable<CodeInstruction> GenderButtonTranspiler(IEnumerable<CodeInstruction> instructions, OpCode loadPawn, Type type = null)
        {
            bool malefound = false;
            foreach (CodeInstruction code in instructions)
            {
                yield return code;
                if (code.LoadsConstant("Male"))
                {
                    malefound = true;
                }
                if (malefound && code.Calls(typeof(List<FloatMenuOption>), nameof(List<FloatMenuOption>.Add)))
                {
                    yield return new CodeInstruction(OpCodes.Nop);
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(loadPawn);
                    if (type != null)
                    {
                        yield return CodeInstruction.LoadField(type, "pawn");
                    }
                    yield return CodeInstruction.Call(typeof(PawnEditorPatches), nameof(DoButtonsHelper));
                    yield return code;
                    malefound = false;
                }
            }
        }

        public static IEnumerable<CodeInstruction> DoButtonsTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return GenderButtonTranspiler(instructions, OpCodes.Ldarg_2);
        }

        //Fork's DrawSexButton is an instance method taking the pawn as its first parameter
        public static IEnumerable<CodeInstruction> DrawSexButtonTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return GenderButtonTranspiler(instructions, OpCodes.Ldarg_1);
        }

        public static IEnumerable<CodeInstruction> DoLeftSectionTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return GenderButtonTranspiler(instructions, OpCodes.Ldarg_0, typeof(Dialog_AppearanceEditor));
        }

        private static FloatMenuOption DoButtonsHelper(Pawn pawn)
        {
            return new FloatMenuOption("NBLabel".Translate().CapitalizeFirst(), delegate
            {
                TabWorker_Bio_Humanlike.SetGender(pawn, (Gender)3);
            }, EnbyUtility.NonBinaryIcon, Color.white);
        }

        public static IEnumerable<CodeInstruction> HeadTypeTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg)
        {
            Label label = ilg.DefineLabel();
            bool insert = false;
            foreach (CodeInstruction code in instructions)
            {
                if (insert)
                {
                    yield return new CodeInstruction(OpCodes.Beq, label);
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return CodeInstruction.Call(typeof(EnbyUtility), nameof(EnbyUtility.IsEnby), [typeof(Pawn)]);
                    yield return new CodeInstruction(OpCodes.Ret);
                    yield return new CodeInstruction(OpCodes.Ldc_I4_1).WithLabels(label);
                    insert = false;
                }
                else
                {
                    yield return code;
                }

                if (code.LoadsField(InfoHelper.genderField))
                {
                    insert = true;
                }
            }
        }

        public static bool HeadTypePredicatePrefix(Pawn ___pawn, ref bool __result)
        {
            if (___pawn.IsEnby())
            {
                __result = true;
                return false;
            }
            return true;
        }

        //Same as HeadTypePredicatePrefix, for the fork's closure version where the editor is captured as <>4__this
        public static bool HeadTypeClosurePredicatePrefix(object __instance, ref bool __result)
        {
            Dialog_AppearanceEditor editor = Traverse.Create(__instance).Field("<>4__this").GetValue<Dialog_AppearanceEditor>();
            Pawn pawn = editor == null ? null : Traverse.Create(editor).Field("pawn").GetValue<Pawn>();
            return pawn == null || HeadTypePredicatePrefix(pawn, ref __result);
        }
    }
}
