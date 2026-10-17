using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const string ExperienceEffect = "FateCoop_Experience", ExperienceText = "% Experience", GemIcon = "sagestone", GemIconSource = "ruby";
    private const float GemHue = 0.86f;
    private static readonly (string Source, string Key, string Title, int Percent)[] ExperienceGems =
    {
        ("Item_CrackedRuby", "Item_CrackedSagestone", "Cracked Sagestone", 2), ("Item_Ruby", "Item_Sagestone", "Sagestone", 4),
        ("Item_SuperiorRuby", "Item_SuperiorSagestone", "Superior Sagestone", 6), ("Item_PerfectRuby", "Item_PerfectSagestone", "Perfect Sagestone", 8),
        ("Item_DivineRuby", "Item_DivineSagestone", "Divine Sagestone", 10)
    };
    private static readonly System.Reflection.MethodInfo SetSpriteMethod = AccessTools.Method(typeof(Item), "SetSprite");
    private static readonly AccessTools.FieldRef<Character, Dictionary<ActivationType, List<AppliedEffect>>> EffectsRef = AccessTools.FieldRefAccess<Character, Dictionary<ActivationType, List<AppliedEffect>>>("effects");

    private void InstallExperienceGemHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Character), "AwardExperience"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeAwardExperience)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(EffectData), "IsPositive"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterEffectPositive)));
        harmony.Patch(AccessTools.Method(typeof(EffectData), "GenerateTooltipString"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterEffectTooltip)));
        harmony.Patch(AccessTools.Method(typeof(Item), "FillInstance"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterItemSaved)));
        harmony.Patch(AccessTools.Method(typeof(Item), "ApplyInstance"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterItemLoaded)));
    }

    private static void AfterItemSaved(Item __instance, ItemSaveData pInstance)
    {
        var gem = ExperienceGems.FirstOrDefault(g => g.Key == __instance.itemBaseName);
        if (gem.Key != null) pInstance.baseName = gem.Source;
    }

    private static void AfterItemLoaded(Item __instance)
    {
        var gem = ExperienceGems.FirstOrDefault(g => g.Key == __instance.itemName && g.Source == __instance.itemBaseName);
        if (gem.Key == null) return;
        __instance.itemData = SingletonBehaviour<FateDB>.Instance.AllItems.First(item => item.displayName == gem.Key);
        __instance.itemBaseName = gem.Key;
        SetSpriteMethod.Invoke(__instance, new object[] { __instance.itemData.IconName });
    }

    private void PrepareExperienceGems(FateDB db)
    {
        var gems = ExperienceGems.Select(gem =>
        {
            var source = db.AllItems.First(i => i.displayName == gem.Source);
            var item = CopyItem(source, gem.Key, gem.Title);
            item.iconPath = $"ICONS/{GemIcon}.png:ICONS/{GemIcon}a.png";
            item.itemEffects = new List<EffectData> { new EffectData(source.itemEffects[0]) { name = ExperienceEffect, buffType = EffectType.None, value = gem.Percent, valueBonus = 0f, value2 = 0, value3 = 0 } };
            return item;
        }).ToList();
        AddItems(db, gems);
        var icon = db.spriteStash.First(s => s.name.ToLower() == GemIconSource);
        var trimmed = Recolor(icon.texture, icon.textureRect);
        var picture = new Texture2D((int)icon.rect.width, (int)icon.rect.height, TextureFormat.ARGB32, false);
        picture.SetPixels(new Color[picture.width * picture.height]);
        picture.SetPixels((int)icon.textureRectOffset.x, (int)icon.textureRectOffset.y, trimmed.width, trimmed.height, trimmed.GetPixels());
        picture.Apply();
        var sprite = Sprite.Create(picture, new Rect(0f, 0f, picture.width, picture.height), new Vector2(icon.pivot.x / icon.rect.width, icon.pivot.y / icon.rect.height), icon.pixelsPerUnit);
        sprite.name = GemIcon;
        db.spriteStash = db.spriteStash.Append(sprite).ToArray();
        var model = CopyAsset(db.itemPrefabs.First(p => p.name.Replace("P_", "").ToLower() == GemIconSource), "P_" + GemIcon);
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            renderer.sharedMaterials = renderer.sharedMaterials.Select(material => new Material(material) { mainTexture = Recolor(material.mainTexture, new Rect(0f, 0f, material.mainTexture.width, material.mainTexture.height)) }).ToArray();
        db.itemPrefabs = db.itemPrefabs.Append(model).ToArray();
    }

    private static Texture2D Recolor(Texture source, Rect area)
    {
        var buffer = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, buffer);
        var previous = RenderTexture.active;
        RenderTexture.active = buffer;
        var copy = new Texture2D((int)area.width, (int)area.height, TextureFormat.ARGB32, false);
        copy.ReadPixels(area, 0, 0);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(buffer);
        copy.SetPixels(copy.GetPixels().Select(pixel =>
        {
            Color.RGBToHSV(pixel, out _, out var saturation, out var value);
            var shifted = Color.HSVToRGB(GemHue, saturation, value);
            shifted.a = pixel.a;
            return shifted;
        }).ToArray());
        copy.Apply();
        return copy;
    }

    private static void BeforeAwardExperience(Character __instance, ref int award, bool isLoadSave)
    {
        if (isLoadSave || __instance != Player.PlayerChar || award <= 0) return;
        var bonus = EffectsRef(__instance).TryGetValue(ActivationType.Passive, out var passive) ? passive.Where(e => e.Name == ExperienceEffect).Sum(e => e.Value) : 0;
        award = Mathf.RoundToInt(award * (1f + bonus / 100f));
    }

    private static void AfterEffectPositive(EffectData __instance, ref bool __result)
    {
        if (__instance.name == ExperienceEffect) __result = true;
    }

    private static void AfterEffectTooltip(EffectData __instance, EffectData.TooltipSource source, ref string __result)
    {
        if (__instance.name == ExperienceEffect) __result = (source == EffectData.TooltipSource.Gem ? "" : __instance.value.ToString()) + ExperienceText;
    }
}
