using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CarpetStuff;

public sealed class CarpetStuffMod : Mod
{
    public const string PackageId = "christoph.carpetstuff";

    public CarpetStuffMod(ModContentPack content)
        : base(content)
    {
        RuntimeHelpers.RunClassConstructor(typeof(CarpetStuffState).TypeHandle);
        new Harmony(PackageId).PatchAll();
    }
}

internal sealed class CarpetDropdownData
{
    public DesignatorDropdownGroupDef Group = null!;
    public List<string> BaseDefOrder { get; } = new();
    public Dictionary<ThingDef, Dictionary<string, TerrainDef>> TerrainsByStuff { get; } = new();
}

internal static class CarpetStuffState
{
    private const float DefaultClothFlammability = 1f;
    private const float DefaultClothBeauty = 1f;
    private const float DefaultClothProtectiveness = 0.16f;
    private const float MinimumCleaningScale = 0.25f;
    private const float MaximumCleaningScale = 1.35f;

    private static readonly FieldInfo ElementsField = AccessTools.Field(typeof(Designator_Dropdown), "elements");
    private static readonly FieldInfo ActiveDesignatorField = AccessTools.Field(typeof(Designator_Dropdown), "activeDesignator");
    private static readonly FieldInfo ActiveDesignatorSetField = AccessTools.Field(typeof(Designator_Dropdown), "activeDesignatorSet");
    private static readonly MethodInfo InitializeThingShortHashDictionaryMethod = AccessTools.Method(typeof(DefDatabase<ThingDef>), "InitializeShortHashDictionary");
    private static readonly MethodInfo InitializeTerrainShortHashDictionaryMethod = AccessTools.Method(typeof(DefDatabase<TerrainDef>), "InitializeShortHashDictionary");
    private static readonly MethodInfo ResolveDesignatorsMethod = AccessTools.Method(typeof(DesignationCategoryDef), "ResolveDesignators");

    private static readonly TerrainDef? BurnedCarpetDef = DefDatabase<TerrainDef>.GetNamedSilentFail("BurnedCarpet");
    private static readonly Dictionary<DesignatorDropdownGroupDef, CarpetDropdownData> DropdownData = new();
    private static readonly Dictionary<DesignatorDropdownGroupDef, ThingDef> SelectedStuffByGroup = new();
    private static readonly Dictionary<TerrainDef, string> BaseKeyByTerrain = new();
    private static bool generated;

    static CarpetStuffState()
    {
        LongEventHandler.ExecuteWhenFinished(GenerateMaterialCarpets);
    }

    public static bool TryBuildMaterialOptions(Designator_Dropdown dropdown, out List<FloatMenuOption> options)
    {
        options = new List<FloatMenuOption>();
        if (!TryGetDropdownData(dropdown, out CarpetDropdownData? data))
        {
            return false;
        }

        ThingDef currentStuff = GetSelectedStuff(data!);
        foreach (ThingDef stuff in data!.TerrainsByStuff.Keys.OrderBy(def => def != ThingDefOf.Cloth).ThenBy(def => def.label ?? def.defName))
        {
            ThingDef localStuff = stuff;
            string label = (localStuff == currentStuff ? $"material: {localStuff.LabelCap}  ✓" : $"material: {localStuff.LabelCap}").CapitalizeFirst();
            options.Add(new FloatMenuOption(label, delegate
            {
                ApplyStuffSelection(dropdown, localStuff);
                if (GetActiveDesignator(dropdown) is Designator active)
                {
                    Find.DesignatorManager.Select(active);
                }
            }, localStuff));
        }

        return options.Count > 0;
    }

    public static void PrepareDropdownForSelection(Designator_Dropdown dropdown)
    {
        if (!TryGetDropdownData(dropdown, out CarpetDropdownData? data))
        {
            return;
        }

        ApplyMaterial(dropdown, data!, GetSelectedStuff(data!), GetActiveTerrain(dropdown));
    }

    public static void GenerateMaterialCarpets()
    {
        if (generated)
        {
            return;
        }

        generated = true;
        DropdownData.Clear();
        SelectedStuffByGroup.Clear();
        BaseKeyByTerrain.Clear();

        List<TerrainDef> baseCarpets = DefDatabase<TerrainDef>.AllDefsListForReading
            .Where(IsBaseCarpet)
            .OrderBy(def => def.uiOrder)
            .ThenBy(def => def.defName)
            .ToList();

        List<ThingDef> textileStuffs = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(IsAllowedTextileStuff)
            .OrderBy(def => def != ThingDefOf.Cloth)
            .ThenBy(def => def.label ?? def.defName)
            .ToList();

        if (baseCarpets.Count == 0 || textileStuffs.Count == 0)
        {
            return;
        }

        float clothFlammability = GetThingStat(ThingDefOf.Cloth, StatDefOf.Flammability, DefaultClothFlammability);
        float clothBeauty = Mathf.Max(0.1f, GetThingStat(ThingDefOf.Cloth, StatDefOf.Beauty, DefaultClothBeauty));
        float clothProtectiveness = Mathf.Max(0.01f, GetTotalProtectiveness(ThingDefOf.Cloth, DefaultClothProtectiveness));

        foreach (TerrainDef baseCarpet in baseCarpets)
        {
            RegisterTerrain(baseCarpet, ThingDefOf.Cloth, baseCarpet.defName);
            foreach (ThingDef stuff in textileStuffs)
            {
                if (stuff == ThingDefOf.Cloth)
                {
                    continue;
                }

                TerrainDef clone = CreateTerrainVariant(baseCarpet, stuff, clothFlammability, clothBeauty, clothProtectiveness);
                CreateBuildDefsForTerrain(clone);
                DefGenerator.AddImpliedDef(clone.blueprintDef);
                DefGenerator.AddImpliedDef(clone.frameDef);
                DefGenerator.AddImpliedDef(clone);
                AssignGeneratedShortHash(clone.blueprintDef);
                AssignGeneratedShortHash(clone.frameDef);
                AssignGeneratedShortHash(clone);
                RegisterTerrain(clone, stuff, baseCarpet.defName);
            }
        }

        InitializeThingShortHashDictionaryMethod?.Invoke(null, null);
        InitializeTerrainShortHashDictionaryMethod?.Invoke(null, null);
        RebuildFloorDesignationCategory();
    }

    public static bool IsGeneratedMaterialTerrain(BuildableDef? buildable)
    {
        if (buildable is not TerrainDef terrain)
        {
            return false;
        }

        return BaseKeyByTerrain.TryGetValue(terrain, out string? baseKey)
            && terrain.defName != baseKey
            && terrain.modContentPack?.PackageIdPlayerFacing == CarpetStuffMod.PackageId;
    }

    private static void RegisterTerrain(TerrainDef terrain, ThingDef stuff, string baseKey)
    {
        if (terrain.designatorDropdown == null)
        {
            return;
        }

        if (!DropdownData.TryGetValue(terrain.designatorDropdown, out CarpetDropdownData? data))
        {
            data = new CarpetDropdownData
            {
                Group = terrain.designatorDropdown
            };
            DropdownData.Add(terrain.designatorDropdown, data);
        }

        if (!data.BaseDefOrder.Contains(baseKey))
        {
            data.BaseDefOrder.Add(baseKey);
        }

        if (!data.TerrainsByStuff.TryGetValue(stuff, out Dictionary<string, TerrainDef>? terrainMap))
        {
            terrainMap = new Dictionary<string, TerrainDef>();
            data.TerrainsByStuff.Add(stuff, terrainMap);
        }

        terrainMap[baseKey] = terrain;
        BaseKeyByTerrain[terrain] = baseKey;
        if (!SelectedStuffByGroup.ContainsKey(data.Group))
        {
            SelectedStuffByGroup[data.Group] = ThingDefOf.Cloth;
        }
    }

    private static bool TryGetDropdownData(Designator_Dropdown dropdown, out CarpetDropdownData? data)
    {
        data = null;
        TerrainDef? terrain = GetActiveTerrain(dropdown);
        if (terrain?.designatorDropdown == null)
        {
            return false;
        }

        return DropdownData.TryGetValue(terrain.designatorDropdown, out data);
    }

    private static ThingDef GetSelectedStuff(CarpetDropdownData data)
    {
        if (SelectedStuffByGroup.TryGetValue(data.Group, out ThingDef? stuff))
        {
            return stuff;
        }

        return ThingDefOf.Cloth;
    }

    private static void ApplyStuffSelection(Designator_Dropdown dropdown, ThingDef stuff)
    {
        if (!TryGetDropdownData(dropdown, out CarpetDropdownData? data))
        {
            return;
        }

        TerrainDef? current = GetActiveTerrain(dropdown);
        ApplyMaterial(dropdown, data!, stuff, current);
    }

    private static void ApplyMaterial(Designator_Dropdown dropdown, CarpetDropdownData data, ThingDef stuff, TerrainDef? preferredTerrain)
    {
        if (!data.TerrainsByStuff.TryGetValue(stuff, out Dictionary<string, TerrainDef>? terrainMap))
        {
            terrainMap = data.TerrainsByStuff[ThingDefOf.Cloth];
            stuff = ThingDefOf.Cloth;
        }

        string preferredBaseKey = GetBaseKey(preferredTerrain);
        List<Designator> elements = GetElements(dropdown);
        elements.Clear();
        ActiveDesignatorField.SetValue(dropdown, null);
        ActiveDesignatorSetField.SetValue(dropdown, true);

        Designator? preferredDesignator = null;
        foreach (string baseKey in data.BaseDefOrder)
        {
            TerrainDef terrain = terrainMap.TryGetValue(baseKey, out TerrainDef? matched) ? matched : data.TerrainsByStuff[ThingDefOf.Cloth][baseKey];
            Designator_Build designator = new Designator_Build(terrain);
            dropdown.Add(designator);
            if (baseKey == preferredBaseKey)
            {
                preferredDesignator = designator;
            }
        }

        if (preferredDesignator != null)
        {
            dropdown.SetActiveDesignator(preferredDesignator);
        }

        SelectedStuffByGroup[data.Group] = stuff;
    }

    private static List<Designator> GetElements(Designator_Dropdown dropdown)
    {
        return (List<Designator>)ElementsField.GetValue(dropdown);
    }

    private static Designator? GetActiveDesignator(Designator_Dropdown dropdown)
    {
        return (Designator?)ActiveDesignatorField.GetValue(dropdown);
    }

    private static TerrainDef? GetActiveTerrain(Designator_Dropdown dropdown)
    {
        if (GetActiveDesignator(dropdown) is Designator_Build build && build.PlacingDef is TerrainDef terrain)
        {
            return terrain;
        }

        return null;
    }

    private static string GetBaseKey(TerrainDef? terrain)
    {
        if (terrain != null && BaseKeyByTerrain.TryGetValue(terrain, out string? key))
        {
            return key;
        }

        return terrain?.defName ?? string.Empty;
    }

    private static bool IsBaseCarpet(TerrainDef def)
    {
        if (def == null || def.designationCategory != DesignationCategoryDefOf.Floors)
        {
            return false;
        }

        if (def.designatorDropdown == null || def.burnedDef != BurnedCarpetDef)
        {
            return false;
        }

        if (def.CostList == null || def.CostList.Count != 1 || def.CostList[0].thingDef != ThingDefOf.Cloth)
        {
            return false;
        }

        return def.label?.IndexOf("carpet", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsAllowedTextileStuff(ThingDef def)
    {
        if (def?.stuffProps?.categories == null || !def.IsStuff)
        {
            return false;
        }

        return def.stuffProps.categories.Contains(StuffCategoryDefOf.Fabric)
            || def.stuffProps.categories.Contains(StuffCategoryDefOf.Leathery);
    }

    private static TerrainDef CreateTerrainVariant(TerrainDef baseCarpet, ThingDef stuff, float clothFlammability, float clothBeauty, float clothProtectiveness)
    {
        TerrainDef clone = CloneDef(baseCarpet);
        clone.defName = $"{baseCarpet.defName}_{stuff.defName}";
        clone.label = $"{stuff.label} {baseCarpet.label}";
        clone.description = $"{baseCarpet.description}\n\nMaterial: {stuff.LabelCap}.";
        clone.costList = new List<ThingDefCountClass>
        {
            new ThingDefCountClass(stuff, baseCarpet.CostList[0].count)
        };
        clone.costStuffCount = 0;
        clone.stuffCategories = null;
        clone.canGenerateDefaultDesignator = false;
        clone.modContentPack = LoadedModManager.RunningModsListForReading.FirstOrDefault(mod => mod.PackageIdPlayerFacing == CarpetStuffMod.PackageId);

        List<StatModifier> statBases = CloneStatBases(baseCarpet.statBases);
        float baseFlammability = GetStat(statBases, StatDefOf.Flammability, 0f);
        float baseBeauty = GetStat(statBases, StatDefOf.Beauty, 0f);
        float baseCleaningTime = GetStat(statBases, StatDefOf.CleaningTimeFactor, 1f);

        float stuffFlammability = GetThingStat(stuff, StatDefOf.Flammability, clothFlammability);
        float stuffBeauty = GetThingStat(stuff, StatDefOf.Beauty, clothBeauty);
        float stuffProtectiveness = Mathf.Max(0.01f, GetTotalProtectiveness(stuff, clothProtectiveness));

        float flammabilityScale = clothFlammability > 0.001f ? stuffFlammability / clothFlammability : 1f;
        float beautyScale = clothBeauty > 0.001f ? Mathf.Max(0f, stuffBeauty / clothBeauty) : 1f;
        float cleaningScale = Mathf.Clamp(Mathf.Sqrt(clothProtectiveness / stuffProtectiveness), MinimumCleaningScale, MaximumCleaningScale);

        SetStat(statBases, StatDefOf.Flammability, baseFlammability * flammabilityScale);
        SetStat(statBases, StatDefOf.Beauty, baseBeauty * beautyScale);
        SetStat(statBases, StatDefOf.CleaningTimeFactor, baseCleaningTime * cleaningScale);

        clone.statBases = statBases;
        return clone;
    }

    private static void CreateBuildDefsForTerrain(TerrainDef terrain)
    {
        if (terrain.blueprintDef != null)
        {
            ThingDef blueprint = CloneDef(terrain.blueprintDef);
            blueprint.defName = $"Blueprint_{terrain.defName}";
            blueprint.label = terrain.label + "BlueprintLabelExtra".Translate();
            blueprint.entityDefToBuild = terrain;
            blueprint.modContentPack = terrain.modContentPack;
            terrain.blueprintDef = blueprint;
        }

        if (terrain.frameDef != null)
        {
            ThingDef frame = CloneDef(terrain.frameDef);
            frame.defName = $"Frame_{terrain.defName}";
            frame.label = terrain.label + "FrameLabelExtra".Translate();
            frame.entityDefToBuild = terrain;
            frame.modContentPack = terrain.modContentPack;
            terrain.frameDef = frame;
        }
    }

    private static float GetThingStat(ThingDef thingDef, StatDef statDef, float fallback)
    {
        if (thingDef == null)
        {
            return fallback;
        }

        try
        {
            return thingDef.GetStatValueAbstract(statDef);
        }
        catch
        {
            return fallback;
        }
    }

    private static float GetTotalProtectiveness(ThingDef thingDef, float fallback)
    {
        if (thingDef == null)
        {
            return fallback;
        }

        float total = 0f;
        total += Mathf.Max(0f, GetThingStat(thingDef, StatDefOf.StuffPower_Armor_Sharp, 0f));
        total += Mathf.Max(0f, GetThingStat(thingDef, StatDefOf.StuffPower_Armor_Blunt, 0f));
        total += Mathf.Max(0f, GetThingStat(thingDef, StatDefOf.StuffPower_Armor_Heat, 0f));
        return total > 0f ? total : fallback;
    }

    private static List<StatModifier> CloneStatBases(List<StatModifier>? statBases)
    {
        List<StatModifier> clone = new();
        if (statBases == null)
        {
            return clone;
        }

        foreach (StatModifier statBase in statBases)
        {
            clone.Add(new StatModifier
            {
                stat = statBase.stat,
                value = statBase.value
            });
        }

        return clone;
    }

    private static float GetStat(List<StatModifier> statBases, StatDef statDef, float fallback)
    {
        for (int i = 0; i < statBases.Count; i++)
        {
            if (statBases[i].stat == statDef)
            {
                return statBases[i].value;
            }
        }

        return fallback;
    }

    private static void SetStat(List<StatModifier> statBases, StatDef statDef, float value)
    {
        for (int i = 0; i < statBases.Count; i++)
        {
            if (statBases[i].stat == statDef)
            {
                statBases[i].value = value;
                return;
            }
        }

        statBases.Add(new StatModifier
        {
            stat = statDef,
            value = value
        });
    }

    private static T CloneDef<T>(T source) where T : Def, new()
    {
        T clone = new T();
        Type? current = source.GetType();
        while (current != null && typeof(Def).IsAssignableFrom(current))
        {
            foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                field.SetValue(clone, field.GetValue(source));
            }

            current = current.BaseType;
        }

        clone.shortHash = 0;
        return clone;
    }

    private static void AssignGeneratedShortHash(ThingDef def)
    {
        if (def.shortHash != 0)
        {
            return;
        }

        HashSet<ushort> used = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(existing => existing != def && existing.shortHash != 0)
            .Select(existing => existing.shortHash)
            .ToHashSet();

        ushort hash = (ushort)(GenText.StableStringHash("CarpetStuff:" + def.defName) % 65535);
        while (hash == 0 || used.Contains(hash))
        {
            hash++;
        }

        def.shortHash = hash;
    }

    private static void AssignGeneratedShortHash(TerrainDef def)
    {
        if (def.shortHash != 0)
        {
            return;
        }

        HashSet<ushort> used = DefDatabase<TerrainDef>.AllDefsListForReading
            .Where(existing => existing != def && existing.shortHash != 0)
            .Select(existing => existing.shortHash)
            .ToHashSet();

        ushort hash = (ushort)(GenText.StableStringHash("CarpetStuff:" + def.defName) % 65535);
        while (hash == 0 || used.Contains(hash))
        {
            hash++;
        }

        def.shortHash = hash;
    }

    private static void RebuildFloorDesignationCategory()
    {
        DesignationCategoryDef? floors = DesignationCategoryDefOf.Floors;
        if (floors == null)
        {
            return;
        }

        floors.DirtyCache();
        ResolveDesignatorsMethod?.Invoke(floors, null);
    }
}

public sealed class CarpetStuffInjectorDef : Def
{
    public override void ResolveReferences()
    {
        base.ResolveReferences();
    }
}

[HarmonyPatch(typeof(Designator_Dropdown), nameof(Designator_Dropdown.ProcessInput))]
internal static class DesignatorDropdownProcessInputPatch
{
    private static void Prefix(Designator_Dropdown __instance, Event ev)
    {
        if (ev.button == 0)
        {
            CarpetStuffState.PrepareDropdownForSelection(__instance);
        }
    }
}

[HarmonyPatch(typeof(Designator), nameof(Designator.RightClickFloatMenuOptions), MethodType.Getter)]
internal static class DesignatorRightClickFloatMenuOptionsPatch
{
    private static void Postfix(Designator __instance, ref IEnumerable<FloatMenuOption> __result)
    {
        if (__instance is not Designator_Dropdown dropdown)
        {
            return;
        }

        if (!CarpetStuffState.TryBuildMaterialOptions(dropdown, out List<FloatMenuOption>? options))
        {
            return;
        }

        __result = __result.Concat(options);
    }
}

[HarmonyPatch(typeof(Ideo), nameof(Ideo.MembersCanBuild))]
internal static class IdeoMembersCanBuildPatch
{
    private static void Postfix(Thing thing, ref bool __result)
    {
        if (__result)
        {
            return;
        }

        if (CarpetStuffState.IsGeneratedMaterialTerrain(thing?.def?.entityDefToBuild))
        {
            __result = true;
        }
    }
}
