using HarmonyLib;
using System;
using System.Reflection;
using Verse;

namespace NonBinaryGender.Patches
{
    [HarmonyPatch]
    public static class Enum_Patches
    {
        //RimWorld 1.7+ runs on CoreCLR, where enum names live in Enum.EnumInfo<TStorage> instead of Mono's ValuesAndNames
        internal static readonly bool IsCoreClr = typeof(Enum).GetNestedType("EnumInfo`1", BindingFlags.NonPublic) != null;

        //The patches below target Mono internals, so skip them on CoreCLR and use AddEnbyToEnumInfo instead
        [HarmonyPrepare]
        public static bool Prepare() => !IsCoreClr;

        //CoreCLR inlines its EnumInfo lookup everywhere, so nothing can be patched
        //Instead, replace the cached EnumInfo for Gender with one that includes Enby
        //This covers GetName, ToString, Parse, GetValues and GetNames, same as the Mono patches
        private static object genderTypeCache;

        internal static void AddEnbyToEnumInfo()
        {
            try
            {
                if (Enum.IsDefined(typeof(Gender), (byte)3))
                {
                    return;
                }
                Type enumInfo = typeof(Enum).GetNestedType("EnumInfo`1", BindingFlags.NonPublic).MakeGenericType(typeof(byte));
                Array oldValues = Enum.GetValues(typeof(Gender));
                string[] oldNames = Enum.GetNames(typeof(Gender));
                int length = oldValues.Length;
                byte[] newValues = new byte[length + 1];
                string[] newNames = new string[length + 1];
                for (int i = 0; i < length; i++)
                {
                    newValues[i] = Convert.ToByte(oldValues.GetValue(i));
                    newNames[i] = oldNames[i];
                }
                newValues[length] = (byte)length;
                newNames[length] = "Enby";
                ConstructorInfo ctor = enumInfo.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(bool), typeof(byte[]), typeof(string[])], null);
                object info = ctor.Invoke([false, newValues, newNames]);
                MethodInfo replace = typeof(Gender).GetType().GetMethod("ReplaceCacheEntry", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(enumInfo);
                replace.Invoke(typeof(Gender), [info]);
                //RuntimeType only holds its cache through a weak handle, so the first GC would discard our EnumInfo
                //and rebuild it from metadata without Enby. Keep the cache alive for the lifetime of the game
                genderTypeCache = typeof(Gender).GetType().GetProperty("Cache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(typeof(Gender));
            }
            catch (Exception e)
            {
                Log.Error($"[NonBinaryGender] Failed to add Enby to the Gender enum: {e}");
            }
        }

        //This has been used by HAR to parse graphic file names
        //This is also used when saving the gender to the save file
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Enum), nameof(Enum.GetName))]
        public static bool EnumGetNamePatch(Type enumType, object value, ref string __result)
        {
            if (enumType == typeof(Gender))
            {
                if (Convert.ToInt32(value) == 3)
                {
                    __result = "Enby";
                    return false;
                }
            }
            return true;
        }

        private static Type valuesAndNames = AccessTools.TypeByName("ValuesAndNames");

        private static object genderCache;

        //This enables "Enby" to be read from the save file and turned into 3
        //It also causes Enum.GetValues && Enum.GetNames to return 'correct' data without needing additional patches
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Enum), "GetCachedValuesAndNames")]
        public static void GetCachedValuesAndNamesPatch(ref object __result, Type enumType)
        {
            if (enumType == typeof(Gender))
            {
                if (genderCache == null)
                {
                    ConstructorInfo ctor = AccessTools.Constructor(valuesAndNames, parameters: [typeof(ulong[]), typeof(string[])]);
                    ulong[] values = (ulong[])AccessTools.Field(valuesAndNames, "Values").GetValue(__result);
                    string[] names = (string[])AccessTools.Field(valuesAndNames, "Names").GetValue(__result);
                    int length = values.Length;
                    ulong[] newValues = new ulong[length + 1];
                    string[] newNames = new string[length + 1];
                    for (int i = 0; i < length; i++)
                    {
                        newValues[i] = values[i];
                        newNames[i] = names[i];
                    }
                    newValues[length] = (uint)length;
                    newNames[length] = "Enby";
                    genderCache = ctor.Invoke([newValues, newNames]);
                }
                __result = genderCache;
            }
        }
    }
}