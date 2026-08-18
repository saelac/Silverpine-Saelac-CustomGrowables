#nullable enable

using Newtonsoft.Json;
using HarmonyLib;
using Silverpine.ModdingTools;
using SilverpineMods.CustomItemLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SilverpineMods.CustomGrowables;

internal static class GrowableRegistry
{
    private static readonly Dictionary<string, GrowableDefinition> ById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite> CustomSprites =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Sprite>? baseGameSprites;

    internal static int Count => ById.Count;

    internal static IReadOnlyList<string> GetBaseGameSpriteNames()
    {
        baseGameSprites ??= BuildBaseGameSpriteCatalog();
        return baseGameSprites.Keys
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void RegisterAll(IReadOnlyList<CustomItemInfo> items)
    {
        foreach (CustomItemInfo item in items)
        {
            if (ById.ContainsKey(item.QualifiedId))
                continue;
            try
            {
                RegisterItem(item);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Skipped growable '{item.QualifiedId}' in " +
                    $"'{item.SourceJsonPath}': {exception}");
            }
        }
    }

    internal static bool TryGet(
        string qualifiedId,
        out GrowableDefinition definition) =>
        ById.TryGetValue(NormalizeId(qualifiedId), out definition!);

    internal static bool TryCreateItem(string identifier, out Item item)
    {
        if (CustomItemApi.TryCreateItem(identifier, out item))
            return true;

        Item? template = ItemLibrary.Items.FirstOrDefault(candidate =>
            candidate != null && string.Equals(
                candidate.name,
                identifier,
                StringComparison.OrdinalIgnoreCase));
        if (template != null)
        {
            item = template.DeepClone();
            return true;
        }

        item = null!;
        return false;
    }

    internal static GameObject Plant(
        GrowableDefinition definition,
        Vector2Int position)
    {
        GameObject instance = UnityEngine.Object.Instantiate(
            definition.Template,
            position.ToVector3(definition.Template.transform.position.z),
            definition.Template.transform.rotation);
        instance.hideFlags = HideFlags.None;
        if (!instance.activeSelf)
            instance.SetActive(true);
        return instance;
    }

    private static void RegisterItem(CustomItemInfo itemInfo)
    {
        string? json = CustomItemApi.GetItemExtensionJson(
            itemInfo.QualifiedId,
            "growable");
        if (string.IsNullOrWhiteSpace(json))
            return;

        GrowableDefinition definition =
            JsonConvert.DeserializeObject<GrowableDefinition>(json!) ??
            throw new InvalidDataException(
                "The growable extension is not a JSON object.");
        string qualifiedId = itemInfo.QualifiedId;
        if (itemInfo.Template.TryGetItemComponent<ItemComponent_Seeds>(out _))
            throw new InvalidDataException(
                "A custom growable seed cannot clone a native seed or " +
                "sapling item because that would activate both planting " +
                "behaviors. Use a component-free clone or no clone.");

        if (string.IsNullOrWhiteSpace(definition.visibleName))
            definition.visibleName = DeriveVisibleName(itemInfo.DisplayName);
        definition.Validate(itemInfo.SourceJsonPath, qualifiedId);
        definition.DisplayName = definition.visibleName!.Trim();
        definition.PrefabName = CreatePrefabName(qualifiedId);

        Plugin.Log.LogInfo($"Preparing growable '{qualifiedId}'.");
        List<int> pendingGlbStages = ResolveSprites(
            definition,
            itemInfo.GetSprite());
        ValidateHarvestItems(definition);
        definition.Template = CreateTemplate(definition);
        SerializablePrefabRegistration registration =
            SerializablePrefabs.Register(
                Plugin.PluginGuid,
                definition.PrefabName,
                definition.Template,
                new SerializablePrefabOptions
                {
                    RequireNpcVisibleObject = true,
                    OnInstanceRestored = RestoreInstance
                });

        ById.Add(qualifiedId, definition);
        try
        {
            CustomItemApi.AddTemplateComponent(
                Plugin.PluginGuid,
                qualifiedId,
                new ItemComponent_CustomGrowableSeed(qualifiedId));
        }
        catch
        {
            ById.Remove(qualifiedId);
            registration.Unregister(destroyTemplate: true);
            throw;
        }

        Plugin.Log.LogInfo(
            $"Registered growable '{qualifiedId}' with " +
            $"{definition.stages!.Count} stages.");
        if (pendingGlbStages.Count > 0)
            RenderPendingGlbStagesAsync(definition, pendingGlbStages);
    }

    private static List<int> ResolveSprites(
        GrowableDefinition definition,
        Sprite fallback)
    {
        var pendingGlbStages = new List<int>();
        for (int index = 0; index < definition.stages!.Count; index++)
        {
            GrowableStageDefinition stage = definition.stages[index];
            if (!string.IsNullOrWhiteSpace(stage.sprite))
            {
                stage.RuntimeSprite = ResolveBaseGameSprite(stage.sprite!);
            }
            else if (!string.IsNullOrWhiteSpace(stage.image))
            {
                stage.RuntimeSprite = LoadCustomSprite(
                    stage.image!,
                    definition.QualifiedId + ":stage:" + index,
                    stage);
            }
            else if (TryLoadGlbStageCache(
                         definition,
                         index,
                         stage,
                         out Sprite cached))
            {
                stage.RuntimeSprite = cached;
            }
            else
            {
                stage.RuntimeSprite = index > 0
                    ? definition.stages[index - 1].RuntimeSprite
                    : fallback;
                pendingGlbStages.Add(index);
            }
        }
        return pendingGlbStages;
    }

    private static bool TryLoadGlbStageCache(
        GrowableDefinition definition,
        int index,
        GrowableStageDefinition stage,
        out Sprite sprite)
    {
        GetGlbStageCachePaths(
            definition,
            index,
            out string imagePath,
            out string keyPath);
        sprite = null!;
        try
        {
            if (!File.Exists(imagePath) || !File.Exists(keyPath) ||
                !string.Equals(
                    File.ReadAllText(keyPath).Trim(),
                    ComputeGlbStageCacheKey(stage),
                    StringComparison.Ordinal))
                return false;
            sprite = LoadCustomSprite(
                imagePath,
                definition.QualifiedId + ":stage:" + index + ":glb-cache",
                stage);
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                $"Ignoring invalid growable GLB cache '{imagePath}': " +
                exception.Message);
            return false;
        }
    }

    private static async void RenderPendingGlbStagesAsync(
        GrowableDefinition definition,
        IReadOnlyList<int> stageIndexes)
    {
        foreach (int index in stageIndexes)
        {
            GrowableStageDefinition stage = definition.stages![index];
            try
            {
                string spriteName = definition.QualifiedId + ":stage:" +
                    index + ":glb";
                Sprite rendered = await CustomItemApi.RenderGlbSpriteAsync(
                    stage.model!,
                    stage.ModelRotation,
                    stage.zoom,
                    stage.resolution,
                    spriteName);
                TryWriteGlbStageCache(definition, index, stage, rendered);
                Sprite adjusted = ApplyStageSpriteSettings(rendered, stage);
                stage.RuntimeSprite = adjusted;
                RefreshDefinitionArt(definition, index, adjusted);
                Plugin.Log.LogInfo(
                    $"Rendered one-direction GLB sprite for growable " +
                    $"'{definition.QualifiedId}' stage {index}.");
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Could not render GLB sprite for growable " +
                    $"'{definition.QualifiedId}' stage {index}; its current " +
                    $"fallback sprite will remain active: {exception}");
            }
        }
    }

    private static Sprite ApplyStageSpriteSettings(
        Sprite rendered,
        GrowableStageDefinition stage)
    {
        Texture2D texture = rendered.texture;
        Sprite adjusted = Sprite.Create(
            texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(stage.pivotX, stage.pivotY),
            stage.pixelsPerUnit);
        adjusted.name = rendered.name;
        UnityEngine.Object.Destroy(rendered);
        return adjusted;
    }

    private static void RefreshDefinitionArt(
        GrowableDefinition definition,
        int stageIndex,
        Sprite sprite)
    {
        if (stageIndex == 0 && definition.Template != null)
        {
            SpriteRenderer? renderer =
                definition.Template.GetComponent<SpriteRenderer>();
            if (renderer != null)
                renderer.sprite = sprite;
        }
        foreach (CustomGrowable growable in
                 Resources.FindObjectsOfTypeAll<CustomGrowable>())
            growable.RefreshArt(definition.QualifiedId);
    }

    private static void TryWriteGlbStageCache(
        GrowableDefinition definition,
        int index,
        GrowableStageDefinition stage,
        Sprite rendered)
    {
        GetGlbStageCachePaths(
            definition,
            index,
            out string imagePath,
            out string keyPath);
        string imageTemporary = imagePath + ".tmp";
        string keyTemporary = keyPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            File.WriteAllBytes(imageTemporary, rendered.texture.EncodeToPNG());
            File.WriteAllText(keyTemporary, ComputeGlbStageCacheKey(stage));
            File.Copy(imageTemporary, imagePath, overwrite: true);
            File.Copy(keyTemporary, keyPath, overwrite: true);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                $"Could not cache growable GLB stage '{imagePath}': " +
                exception.Message);
        }
        finally
        {
            TryDeleteTemporaryCacheFile(imageTemporary);
            TryDeleteTemporaryCacheFile(keyTemporary);
        }
    }

    private static void TryDeleteTemporaryCacheFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                $"Could not remove temporary growable cache file '{path}': " +
                exception.Message);
        }
    }

    private static void GetGlbStageCachePaths(
        GrowableDefinition definition,
        int index,
        out string imagePath,
        out string keyPath)
    {
        string cacheDirectory = Path.Combine(definition.PackDirectory, ".cache");
        string cacheName = definition.PrefabName + "__stage_" + index;
        imagePath = Path.Combine(cacheDirectory, cacheName + ".png");
        keyPath = Path.Combine(cacheDirectory, cacheName + ".key");
    }

    private static string ComputeGlbStageCacheKey(
        GrowableStageDefinition stage)
    {
        using SHA256 sha = SHA256.Create();
        byte[] modelHash;
        using (FileStream stream = File.OpenRead(stage.model!))
            modelHash = sha.ComputeHash(stream);
        Vector3 rotation = stage.ModelRotation;
        string settings = CustomItemApi.GlbSpriteRendererVersion + "|" +
            Convert.ToBase64String(modelHash) + "|" +
            rotation.x.ToString("R", CultureInfo.InvariantCulture) + "|" +
            rotation.y.ToString("R", CultureInfo.InvariantCulture) + "|" +
            rotation.z.ToString("R", CultureInfo.InvariantCulture) + "|" +
            stage.zoom.ToString("R", CultureInfo.InvariantCulture) + "|" +
            stage.resolution + "|" +
            stage.pixelsPerUnit.ToString("R", CultureInfo.InvariantCulture) +
            "|" + stage.pivotX.ToString("R", CultureInfo.InvariantCulture) +
            "|" + stage.pivotY.ToString("R", CultureInfo.InvariantCulture);
        return Convert.ToBase64String(
            sha.ComputeHash(Encoding.UTF8.GetBytes(settings)));
    }

    private static Sprite ResolveBaseGameSprite(string nameOrPath)
    {
        if (nameOrPath.Contains('/'))
        {
            Sprite? direct = Resources.Load<Sprite>(nameOrPath);
            if (direct != null)
                return direct;
        }

        baseGameSprites ??= BuildBaseGameSpriteCatalog();
        if (baseGameSprites.TryGetValue(nameOrPath, out Sprite sprite))
            return sprite;
        throw new InvalidDataException(
            $"Base-game sprite '{nameOrPath}' was not found. Use its exact " +
            "asset name from a loaded native prefab, or an explicit Resources " +
            "path when the sprite is not used by a prefab.");
    }

    private static Dictionary<string, Sprite> BuildBaseGameSpriteCatalog()
    {
        var result = new Dictionary<string, Sprite>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SpriteRenderer renderer in
                 Resources.FindObjectsOfTypeAll<SpriteRenderer>())
        {
            Sprite? sprite = renderer?.sprite;
            if (sprite != null && !string.IsNullOrWhiteSpace(sprite.name))
                result.TryAdd(sprite.name, sprite);
        }

        FieldInfo? matureSpriteField = AccessTools.Field(
            typeof(Crop),
            "matureSprite");
        if (matureSpriteField != null)
            foreach (Crop crop in Resources.FindObjectsOfTypeAll<Crop>())
            {
                Sprite? sprite = matureSpriteField.GetValue(crop) as Sprite;
                if (sprite != null && !string.IsNullOrWhiteSpace(sprite.name))
                    result.TryAdd(sprite.name, sprite);
            }

        Plugin.Log.LogInfo(
            $"Indexed {result.Count} base-game prefab sprite assets for " +
            "growable stage lookup.");
        return result;
    }

    private static Sprite LoadCustomSprite(
        string path,
        string spriteName,
        GrowableStageDefinition stage)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unsupported growable image format '{extension}'. Use PNG, " +
                "JPG, or JPEG.");

        string cacheKey = path + "|" + stage.pixelsPerUnit + "|" +
            stage.pivotX + "|" + stage.pivotY;
        if (CustomSprites.TryGetValue(cacheKey, out Sprite cached))
            return cached;

        Texture2D texture = new(2, 2, TextureFormat.ARGB32, false);
        if (!texture.LoadImage(File.ReadAllBytes(path), false))
        {
            UnityEngine.Object.Destroy(texture);
            throw new InvalidDataException(
                "Unity could not decode growable image: " + path);
        }
        texture.name = spriteName;
        texture.filterMode = FilterMode.Point;
        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(stage.pivotX, stage.pivotY),
            stage.pixelsPerUnit);
        sprite.name = spriteName;
        CustomSprites.Add(cacheKey, sprite);
        return sprite;
    }

    private static void ValidateHarvestItems(GrowableDefinition definition)
    {
        foreach (GrowableHarvestDefinition output in definition.harvest!)
            if (!TryCreateItem(output.item!, out _))
                throw new InvalidDataException(
                    $"Harvest item '{output.item}' does not name a registered " +
                    "custom item ID or base-game item.");
    }

    private static GameObject CreateTemplate(GrowableDefinition definition)
    {
        GameObject template = new(definition.PrefabName);
        template.SetActive(false);
        template.transform.localScale = Vector3.one * definition.scale *
            definition.stages![0].scale;

        SpriteRenderer renderer = template.AddComponent<SpriteRenderer>();
        renderer.sprite = definition.stages![0].RuntimeSprite;

        template.AddComponent<TurfRegistrar>();
        if (definition.blocksMovement)
        {
            TurfCollider collider = template.AddComponent<TurfCollider>();
            collider.passable = false;
            collider.abyss = false;
        }
        CustomGrowable growable = template.AddComponent<CustomGrowable>();
        growable.Configure(definition.QualifiedId);
        template.AddComponent<YSorter>();
        return template;
    }

    private static void RestoreInstance(GameObject instance)
    {
        CustomGrowable? growable = instance.GetComponent<CustomGrowable>();
        if (growable == null)
            throw new InvalidOperationException(
                $"Restored growable prefab '{instance.name}' has no " +
                $"{nameof(CustomGrowable)} component.");
        growable.RestoreInstance();
    }

    private static string DeriveVisibleName(string itemName)
    {
        string value = itemName.Trim();
        if (value.StartsWith("Bag of ", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("Bag of ".Length);
        if (value.EndsWith(" Seeds", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - " Seeds".Length);
        else if (value.EndsWith(" Seed", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - " Seed".Length);
        else if (value.EndsWith(" Sapling", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - " Sapling".Length);
        return string.IsNullOrWhiteSpace(value) ? itemName.Trim() : value;
    }

    private static string CreatePrefabName(string qualifiedId)
    {
        var sanitized = new StringBuilder();
        foreach (char character in qualifiedId)
            sanitized.Append(char.IsLetterOrDigit(character) ? character : '_');
        string safe = sanitized.ToString();
        if (safe.Length > 64)
            safe = safe.Substring(0, 64);

        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(qualifiedId));
        var suffix = new StringBuilder(12);
        for (int index = 0; index < 6; index++)
            suffix.Append(hash[index].ToString("x2"));
        return "customgrowable_crop_" + safe + "_" + suffix;
    }

    private static string NormalizeId(string value)
    {
        string normalized = value?.Trim() ?? "";
        return normalized.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring("custom:".Length)
            : normalized;
    }
}
