#nullable enable

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Silverpine.ModdingTools;
using SilverpineMods.CustomItemLoader;
using System;
using System.IO;

namespace SilverpineMods.CustomGrowables;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    "renegadex.silverpine.customitemloader",
    "2.7.0")]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid =
        "renegadex.silverpine.customgrowables";
    public const string PluginName = "Custom Growables";
    public const string PluginVersion = "1.4.0";

    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;
        Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);
        ModdingToolsMenu.RegisterSession(
            PluginGuid + ".growable-editor",
            "Growables Editor",
            (_, session) => GrowableEditorUI.Open(session),
            order: 320);
        Logger.LogInfo(
            "Registered Growables Editor with the Modding Tools main menu.");
        CustomItemApi.ItemRegistered += OnItemRegistered;
        GrowableRegistry.RegisterAll(CustomItemApi.GetRegisteredItems());
        Logger.LogInfo(
            $"Loaded {PluginName} {PluginVersion}; registered " +
            $"{GrowableRegistry.Count} growable" +
            (GrowableRegistry.Count == 1 ? "." : "s."));
    }

    private static void OnItemRegistered(
        object? sender,
        CustomItemRegisteredEventArgs arguments)
    {
        // Re-read the stable snapshot so a growable skipped only because one
        // of its referenced items was deferred can succeed on this pass.
        GrowableRegistry.RegisterAll(CustomItemApi.GetRegisteredItems());
    }

}

/// <summary>
/// Silverpine normally writes only Type.FullName for item components, which
/// Type.GetType cannot resolve from a plugin assembly. Write an
/// assembly-qualified name for this add-on's component while leaving every
/// native and third-party serializable on the original code path.
/// </summary>
[HarmonyPatch(
    typeof(TypeSerializationUtility),
    nameof(TypeSerializationUtility.SerializeWithType))]
internal static class GrowableItemComponentTypeSerializationPatch
{
    private static bool Prefix(
        ISerializable serializable,
        BinaryWriter binaryWriter)
    {
        if (serializable is not ItemComponent_CustomGrowableSeed)
            return true;

        Type type = serializable.GetType();
        binaryWriter.Write(
            type.FullName + ", " + type.Assembly.GetName().Name);
        serializable.Serialize(binaryWriter);
        return false;
    }
}
