#nullable enable

using System.IO;
using UnityEngine;

namespace SilverpineMods.CustomGrowables;

public sealed class ItemComponent_CustomGrowableSeed : ItemComponent
{
    private string growableId = "";

    public ItemComponent_CustomGrowableSeed()
    {
    }

    internal ItemComponent_CustomGrowableSeed(string growableId)
    {
        this.growableId = growableId;
    }

    public override void Serialize(BinaryWriter binaryWriter)
    {
        var storage = new VersionSafeStorage();
        storage.AddToStorage("growableId", growableId);
        storage.Serialize(binaryWriter);
    }

    public override void Deserialize(BinaryReader binaryReader)
    {
        var storage = new VersionSafeStorage();
        storage.Deserialize(binaryReader);
        growableId = storage.GetString("growableId", growableId);
    }

    public override string GetDescriptionSuffix()
    {
        if (!GrowableRegistry.TryGet(
                growableId,
                out GrowableDefinition definition))
            return "";

        string description =
            $"Grows into {definition.DisplayName.ToLowerInvariant()} in " +
            $"<color=\"yellow\">{definition.GetDurationDescription()}</color>.";
        if (definition.playerPropertyOnly)
            description += "\nCan only be planted on your own property.";
        if (!definition.growsInWinter)
            description += "\nGrowth pauses during winter.";
        return description;
    }

    public override void OnUse()
    {
        if (!GrowableRegistry.TryGet(
                growableId,
                out GrowableDefinition definition))
        {
            UpperNotificationUI.Instance.OneOff(
                "This growable definition is unavailable.");
            return;
        }

        Vector2Int position = Player.Instance.transform.GetVector2IntPosition();
        bool propertyAllowed = !definition.playerPropertyOnly ||
            WorldInfoManager.Instance.IsOnPlayerOwnedPlot(position);
        bool roofAllowed = definition.allowRoofed ||
            !TileTags.HasTileTag(position, TileTag.Roofed);
        if (!propertyAllowed || !roofAllowed)
        {
            UpperNotificationUI.Instance.OneOff("You can't do that here.");
            return;
        }

        AudioPlayer.Instance.PlayLoopAtPosition(
            "sound_loop_shoveling",
            1f,
            position);
        Player.Instance.StartLongInteraction(
            () =>
            {
                if (definition.replaceExistingPlants)
                    PlayerAbility_Construct.DestroyPlantAtPosition(position);
                GrowableRegistry.Plant(definition, position);
                item.RemoveItemFromInventory();
            },
            definition.interactionTurns,
            null,
            () => AudioPlayer.Instance.StopLoop(position));
    }
}
