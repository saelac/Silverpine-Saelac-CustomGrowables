#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace SilverpineMods.CustomGrowables;

public sealed class CustomGrowable :
    MonoBehaviour,
    ISerializableMonoBehavior,
    IInteractionHandler,
    INPCVisibleObject,
    INPCInteractable
{
    [SerializeField]
    private string growableId = "";

    private int stageIndex;
    private float stageProgress;
    private bool hasMatured;
    private bool subscribedToTurns;
    private bool beingRemoved;

    public string InteractionName => "Harvest";

    internal void Configure(string qualifiedId)
    {
        growableId = qualifiedId;
        stageIndex = 0;
        stageProgress = 0f;
        hasMatured = TryGetDefinition(out GrowableDefinition definition) &&
            definition.HasReachedMaturityStage(stageIndex);
        ApplyStage();
    }

    internal void RestoreInstance()
    {
        gameObject.hideFlags = HideFlags.None;
        ApplyStage();
    }

    internal void RefreshArt(string qualifiedId)
    {
        if (growableId.Equals(
                qualifiedId,
                StringComparison.OrdinalIgnoreCase))
            ApplyStage();
    }

    private void Start()
    {
        ApplyStage();
        TrySubscribeToTurns();
    }

    private void FixedUpdate()
    {
        if (!TryGetDefinition(out GrowableDefinition definition))
            return;
        if (definition.Clock == GrowableClock.GameTurns)
        {
            TrySubscribeToTurns();
            return;
        }
        if (!definition.IsHarvestReady(stageIndex) && CanGrowNow(definition))
            Advance(definition, Time.fixedUnscaledDeltaTime);
    }

    private void OnDestroy()
    {
        if (subscribedToTurns && ActionQueue.Instance != null)
            ActionQueue.Instance.OnPostProcessTurn -= OnPostProcessTurn;
        subscribedToTurns = false;
    }

    private void TrySubscribeToTurns()
    {
        if (subscribedToTurns || ActionQueue.Instance == null ||
            !TryGetDefinition(out GrowableDefinition definition) ||
            definition.Clock != GrowableClock.GameTurns)
            return;
        ActionQueue.Instance.OnPostProcessTurn += OnPostProcessTurn;
        subscribedToTurns = true;
    }

    private void OnPostProcessTurn()
    {
        if (!TryGetDefinition(out GrowableDefinition definition) ||
            definition.Clock != GrowableClock.GameTurns ||
            definition.IsHarvestReady(stageIndex) ||
            !CanGrowNow(definition))
            return;
        Advance(definition, 1f);
    }

    private bool CanGrowNow(GrowableDefinition definition)
    {
        if (definition.growsInWinter || WorldInfoManager.Instance == null)
            return true;
        return WorldInfoManager.Instance.GetSeasonFor(gameObject) != Season.Winter;
    }

    private void Advance(GrowableDefinition definition, float amount)
    {
        if (amount <= 0f || definition.IsHarvestReady(stageIndex))
            return;

        stageProgress += amount;
        bool changed = false;
        while (!definition.IsHarvestReady(stageIndex))
        {
            float duration = definition.GetStageDuration(stageIndex);
            if (stageProgress < duration)
                break;
            stageProgress -= duration;
            stageIndex++;
            if (definition.HasReachedMaturityStage(stageIndex))
                hasMatured = true;
            changed = true;
        }
        if (definition.IsHarvestReady(stageIndex))
            stageProgress = 0f;
        if (changed)
            ApplyStage();
    }

    private void ApplyStage()
    {
        if (!TryGetDefinition(out GrowableDefinition definition) ||
            definition.stages == null || definition.stages.Count == 0)
            return;
        stageIndex = Mathf.Clamp(stageIndex, 0, definition.stages.Count - 1);
        GrowableStageDefinition stage = definition.stages[stageIndex];
        transform.localScale = Vector3.one * definition.scale * stage.scale;
        SpriteRenderer? renderer = GetComponent<SpriteRenderer>();
        if (renderer != null)
            renderer.sprite = stage.RuntimeSprite;
        YSorter? sorter = GetComponent<YSorter>();
        if (sorter != null && gameObject.activeInHierarchy)
            sorter.SetSortingOrder();
    }

    public void Serialize(BinaryWriter binaryWriter)
    {
        var storage = new VersionSafeStorage();
        storage.AddToStorage("growableId", growableId);
        storage.AddToStorage("stageIndex", stageIndex);
        storage.AddToStorage("stageProgress", stageProgress);
        storage.AddToStorage("hasMatured", hasMatured);
        storage.AddToStorage(
            "utcNow",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        storage.Serialize(binaryWriter);
    }

    public void Deserialize(BinaryReader binaryReader)
    {
        var storage = new VersionSafeStorage();
        storage.Deserialize(binaryReader);
        growableId = storage.GetString("growableId", growableId);
        stageIndex = storage.GetInt("stageIndex", stageIndex);
        stageProgress = storage.GetFloat("stageProgress", stageProgress);
        bool inferredMaturity = TryGetDefinition(
                out GrowableDefinition maturityDefinition) &&
            maturityDefinition.HasReachedMaturityStage(stageIndex);
        hasMatured = storage.GetBool("hasMatured", inferredMaturity);
        string savedUtc = storage.GetString(
            "utcNow",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        if (TryGetDefinition(out GrowableDefinition definition) &&
            definition.Clock == GrowableClock.RealTimeHours &&
            !definition.IsHarvestReady(stageIndex) &&
            DateTime.TryParse(
                savedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime parsed))
        {
            float elapsed = (float)Math.Max(
                0d,
                DateTime.UtcNow.Subtract(parsed.ToUniversalTime()).TotalSeconds);
            if (CanGrowNow(definition))
                Advance(definition, elapsed);
        }
        ApplyStage();
    }

    public string GetNPCVisibleObjectDescription()
    {
        if (!TryGetDefinition(out GrowableDefinition definition))
            return "An unknown plant";
        if (definition.IsHarvestReady(stageIndex))
            return definition.perennial
                ? "A harvest-ready " +
                    definition.DisplayName.ToLowerInvariant()
                : "A mature " + definition.DisplayName.ToLowerInvariant();
        if (hasMatured)
            return "A mature " + definition.DisplayName.ToLowerInvariant();
        if (stageIndex == 0)
            return "A " + definition.DisplayName.ToLowerInvariant() +
                " seedling";
        return "A growing " + definition.DisplayName.ToLowerInvariant();
    }

    public void OnInteract(GameObject sender)
    {
        if (beingRemoved ||
            !TryGetDefinition(out GrowableDefinition definition))
            return;

        if (!definition.IsHarvestReady(stageIndex))
        {
            DialogBox.Instance.DisplayTextNoDialog(
                "It's not ready to harvest yet. Destroy it?",
                new DialogOption("Yes", BeginDestroyInteraction),
                new DialogOption("No", null));
            return;
        }

        Vector2Int position = transform.GetVector2IntPosition();
        AudioPlayer.Instance.PlayLoopAtPosition(
            "sound_loop_shoveling",
            1f,
            position);
        Player.Instance.StartLongInteraction(
            () => HarvestInto(Player.Instance.worldInventory.inventory),
            definition.interactionTurns,
            null,
            () => AudioPlayer.Instance.StopLoop(position));
    }

    private void BeginDestroyInteraction()
    {
        if (beingRemoved)
            return;
        Vector2Int position = transform.GetVector2IntPosition();
        AudioPlayer.Instance.PlayLoopAtPosition(
            "sound_loop_shoveling",
            1f,
            position);
        Player.Instance.StartLongInteraction(
            RemovePlant,
            TryGetDefinition(out GrowableDefinition definition)
                ? definition.interactionTurns
                : 5,
            null,
            () => AudioPlayer.Instance.StopLoop(position));
    }

    private void HarvestInto(Inventory inventory)
    {
        if (beingRemoved ||
            !TryGetDefinition(out GrowableDefinition definition) ||
            !definition.IsHarvestReady(stageIndex))
            return;

        var overflow = new List<Item>();
        foreach (GrowableHarvestDefinition output in definition.harvest!)
        {
            if (UnityEngine.Random.value > output.chance)
                continue;
            int count = UnityEngine.Random.Range(
                output.minimum,
                output.maximum + 1);
            for (int index = 0; index < count; index++)
            {
                if (!GrowableRegistry.TryCreateItem(output.item!, out Item item))
                {
                    Plugin.Log.LogError(
                        $"Growable '{growableId}' could not create harvest " +
                        $"item '{output.item}'.");
                    continue;
                }
                if (inventory.TryAddItem(item))
                    AnimationHelper.Instance.SpawnItemFloatingText(
                        item,
                        inventory.owner.transform);
                else
                    overflow.Add(item);
            }
        }

        if (overflow.Count > 0)
            LootDropper.DropLoot(
                overflow,
                transform.GetVector2IntPosition());

        AnimationHelper.Instance.SpawnFadeDummy(gameObject);
        if (definition.regrowStage >= 0)
        {
            stageIndex = definition.regrowStage;
            stageProgress = 0f;
            if (!definition.perennial)
                hasMatured = false;
            ApplyStage();
        }
        else
        {
            beingRemoved = true;
            UnityEngine.Object.Destroy(gameObject);
        }
    }

    private void RemovePlant()
    {
        if (beingRemoved)
            return;
        beingRemoved = true;
        AnimationHelper.Instance.SpawnFadeDummy(gameObject);
        UnityEngine.Object.Destroy(gameObject);
    }

    public string GetNPCFunctionCallQuestion(NeuralNPC neuralNPC)
    {
        string name = TryGetDefinition(out GrowableDefinition definition)
            ? definition.DisplayName.ToLowerInvariant()
            : "plant";
        return "In this excerpt, did " + neuralNPC.GetFinalName() +
            " harvest the " + name + "?";
    }

    public string GetNPCFunctionCallAPIName(NeuralNPC neuralNPC)
    {
        string name = TryGetDefinition(out GrowableDefinition definition)
            ? definition.DisplayName
            : "plant";
        return "harvest_" + name.ToLowerInvariant().Replace(" ", "_");
    }

    public bool IsNPCInteractableActive(
        NeuralNPC neuralNPC,
        string conversation)
    {
        return TryGetDefinition(out GrowableDefinition definition) &&
            definition.IsHarvestReady(stageIndex) &&
            conversation.ContainsAnyWordIgnoreCase(
                definition.DisplayName,
                definition.DisplayName + "s");
    }

    public bool IsNPCInteractableAPIActive(NeuralNPC neuralNPC)
    {
        return TryGetDefinition(out GrowableDefinition definition) &&
            definition.IsHarvestReady(stageIndex);
    }

    public Task OnNPCInteract(NeuralNPC neuralNPC)
    {
        if (TryGetDefinition(out GrowableDefinition definition) &&
            definition.IsHarvestReady(stageIndex))
            HarvestInto(neuralNPC.GetComponent<WorldInventory>().inventory);
        return Task.CompletedTask;
    }

    public bool ShouldPathOneWay() => false;

    private bool TryGetDefinition(out GrowableDefinition definition) =>
        GrowableRegistry.TryGet(growableId, out definition);
}
