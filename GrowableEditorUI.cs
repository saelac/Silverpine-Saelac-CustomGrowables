#nullable enable

using Newtonsoft.Json;
using Silverpine.ModdingTools;
using SilverpineMods.CustomItemLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SilverpineMods.CustomGrowables;

/// <summary>
/// A growable-specific editor that deliberately leaves item creation and pack
/// ownership to Custom Item Loader. It reads and writes only the add-on-owned
/// growable extension through CustomItemApi.
/// </summary>
internal sealed class GrowableEditorUI : ModToolBehaviour
{
    private const float DesignWidth = 1920f;
    private const float DesignHeight = 1080f;
    private const float TerrainTileScreenSize = 96f;
    private const string ExtensionName = "growable";

    private static readonly string[] Clocks =
        { nameof(GrowableClock.RealTimeHours), nameof(GrowableClock.GameTurns) };
    private static readonly string[] HarvestModes =
        { "Remove after harvest", "Regrowing crop", "Perennial plant" };

    private static GrowableEditorUI? instance;

    private readonly List<CustomItemInfo> items = new();
    private readonly HashSet<string> growableIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> numberText = new();
    private readonly List<PickerOption> pickerOptions = new();
    private CustomItemInfo? selectedItem;
    private GrowableDefinition? definition;
    private Vector2 itemScroll;
    private Vector2 detailsScroll;
    private Vector2 pickerScroll;
    private string search = "";
    private string status = "";
    private string pickerTitle = "";
    private string pickerCurrent = "";
    private string pickerSearch = "";
    private Action<string>? pickerCallback;
    private string pickerActionLabel = "";
    private Action? pickerAction;
    private bool open;
    private bool dirty;
    private bool filePickerOpen;
    private bool pickerOpen;
    private bool renderingGlbPreview;
    private int glbPreviewStage = -1;
    private Sprite? glbPreview;
    private Sprite? grassTerrainSprite;
    private bool grassTerrainLookupAttempted;

    private sealed class PickerOption
    {
        internal string Value = "";
        internal string Label = "";
        internal string SearchText = "";
    }

    internal static void Open(ModToolSession session)
    {
        if (instance == null)
        {
            GameObject root = new("Custom Growables Editor IMGUI");
            instance = root.AddComponent<GrowableEditorUI>();
        }
        else
        {
            instance.gameObject.SetActive(true);
        }

        instance.AttachSession(session);
        instance.open = true;
        instance.grassTerrainSprite = null;
        instance.grassTerrainLookupAttempted = false;
        instance.RefreshItems();
    }

    private void Update()
    {
        if (filePickerOpen &&
            (GenericListUI.Instance == null ||
             !GenericListUI.Instance.gameObject.activeInHierarchy))
            filePickerOpen = false;

        if (open && !filePickerOpen && Input.GetKeyDown(KeyCode.Escape))
        {
            if (pickerOpen)
                ClosePicker();
            else
                Close();
        }
    }

    private void OnGUI()
    {
        if (!open || filePickerOpen)
            return;

        GUI.enabled = true;
        GUI.color = Color.white;
        GUI.backgroundColor = Color.white;
        GUI.depth = -1000;

        using ModGuiScope scope = ModGui.BeginScaled(DesignWidth, DesignHeight);
        Color old = GUI.color;
        GUI.color = new Color(0.045f, 0.075f, 0.055f, 0.99f);
        GUI.DrawTexture(
            new Rect(0, 0, DesignWidth, DesignHeight),
            Texture2D.whiteTexture);
        GUI.color = old;

        GUILayout.BeginArea(
            new Rect(12, 10, DesignWidth - 24, DesignHeight - 20));
        if (pickerOpen)
        {
            DrawPickerScreen();
            GUILayout.EndArea();
            return;
        }
        DrawHeader();
        GUILayout.BeginHorizontal();
        DrawItemPanel();
        DrawDetailsPanel();
        GUILayout.EndHorizontal();
        if (!string.IsNullOrWhiteSpace(status))
            GUILayout.Label(status, GUI.skin.box);
        GUILayout.EndArea();
    }

    private void DrawHeader()
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label("Growables Editor", GUILayout.Width(220));
        GUILayout.Label(selectedItem == null
            ? "Choose an item registered by Custom Item Loader."
            : selectedItem.QualifiedId + (dirty ? "  • unsaved" : ""));

        GUI.enabled = !dirty;
        if (GUILayout.Button("Refresh CIL", GUILayout.Width(110)))
            RefreshItems();
        GUI.enabled = selectedItem != null && dirty;
        if (GUILayout.Button("Discard", GUILayout.Width(90)))
            LoadSelected();
        GUI.enabled = selectedItem != null && dirty &&
            !(definition != null && SelectedItemInheritsNativeSeeds());
        if (GUILayout.Button("Save Pack", GUILayout.Width(110)))
            SaveSelected();
        GUI.enabled = true;
        if (GUILayout.Button("Close", GUILayout.Width(90)))
            Close();
        GUILayout.EndHorizontal();
    }

    private void DrawItemPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(445));
        GUILayout.Label("Custom Item Loader items");
        GUILayout.Label(
            "Create the seed/inventory item in Custom Item Editor, then " +
            "attach its plant behavior here.",
            GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(62));
        search = GUILayout.TextField(search ?? "");
        if (GUILayout.Button("×", GUILayout.Width(32)))
            search = "";
        GUILayout.EndHorizontal();

        itemScroll = GUILayout.BeginScrollView(itemScroll);
        foreach (CustomItemInfo item in items)
        {
            if (!MatchesSearch(item))
                continue;
            bool selected = ReferenceEquals(selectedItem, item) ||
                selectedItem?.QualifiedId.Equals(
                    item.QualifiedId,
                    StringComparison.OrdinalIgnoreCase) == true;
            string marker = growableIds.Contains(item.QualifiedId) ? "● " : "○ ";
            string inheritedSeed = InheritsNativeSeeds(item)
                ? "  [native seed behavior]"
                : "";
            string label = marker + item.DisplayName + inheritedSeed +
                "\n" + item.QualifiedId;
            Color old = GUI.backgroundColor;
            if (selected)
                GUI.backgroundColor = new Color(0.48f, 0.75f, 0.46f);
            GUI.enabled = !dirty || selected;
            if (GUILayout.Button(label, GUILayout.MinHeight(52)))
                SelectItem(item);
            GUI.enabled = true;
            GUI.backgroundColor = old;
        }
        if (items.Count == 0)
            GUILayout.Label("No registered Custom Item Loader items were found.");
        GUILayout.EndScrollView();
        GUILayout.Label("● has growable data   ○ item only");
        GUILayout.EndVertical();
    }

    private void DrawDetailsPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
        if (selectedItem == null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("Select a Custom Item Loader item.");
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return;
        }

        if (definition == null)
        {
            bool incompatible = SelectedItemInheritsNativeSeeds();
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                selectedItem.DisplayName + " does not have growable behavior.");
            if (incompatible)
            {
                GUILayout.Label(
                    "Growable behavior cannot be attached because this item " +
                    "inherits Silverpine's native ItemComponent_Seeds. " +
                    "Choose a component-free clone or custom artwork.",
                    GUI.skin.box);
            }
            else
            {
                GUILayout.Label(
                    "The item itself remains fully owned by Custom Item Loader.");
            }
            GUI.enabled = !incompatible;
            if (GUILayout.Button("Add Growable Behavior", GUILayout.Height(42)))
                AddDefinition();
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return;
        }

        detailsScroll = GUILayout.BeginScrollView(detailsScroll);
        if (SelectedItemInheritsNativeSeeds())
            GUILayout.Label(
                "This growable cannot be saved because the item inherits " +
                "Silverpine's native seed behavior. Remove Growable Behavior " +
                "below, or recreate the item with a component-free clone.",
                GUI.skin.box);
        DrawLifecycle();
        DrawPlantingRules();
        DrawStages();
        DrawHarvest();
        GUILayout.Space(12);
        if (GUILayout.Button("Remove Growable Behavior", GUILayout.Height(36)))
        {
            definition = null;
            dirty = true;
            numberText.Clear();
            status = "Growable behavior will be removed when the pack is saved.";
        }
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawLifecycle()
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Lifecycle");
        DrawText(
            "Plant name",
            definition!.visibleName ?? "",
            value => Set(ref definition.visibleName, value));
        DrawChoice(
            "Growth clock",
            definition.clock ?? Clocks[0],
            Clocks,
            value =>
            {
                if (definition.clock == value)
                    return;
                definition.clock = value;
                if (Enum.TryParse(value, true, out GrowableClock parsed))
                    definition.Clock = parsed;
                MarkDirty();
            });

        int mode = GetHarvestMode();
        DrawChoice(
            "After harvest",
            HarvestModes[mode],
            HarvestModes,
            value => SetHarvestMode(Array.IndexOf(HarvestModes, value)));

        if (mode != 0)
            DrawStageIndex(
                "Return to stage",
                definition.regrowStage,
                Math.Max(0, (definition.stages?.Count ?? 2) - 2),
                value =>
                {
                    definition.regrowStage = value;
                    if (definition.perennial &&
                        definition.matureStage > value)
                        definition.matureStage = value;
                    MarkDirty();
                });
        if (mode == 2)
        {
            DrawStageIndex(
                "Mature from stage",
                definition.matureStage < 0
                    ? definition.regrowStage
                    : definition.matureStage,
                Math.Max(0, (definition.stages?.Count ?? 1) - 1),
                value =>
                {
                    definition.matureStage = value;
                    if (definition.regrowStage < value)
                        definition.regrowStage = Math.Min(
                            value,
                            Math.Max(0, definition.stages!.Count - 2));
                    MarkDirty();
                });
            GUILayout.Label(
                "A perennial becomes mature once, survives harvest, and " +
                "cycles only through mature stages while its harvest renews.",
                GUI.skin.box);
        }

        DrawInt(
            "Interaction turns",
            "interactionTurns",
            definition.interactionTurns,
            value => Set(ref definition.interactionTurns, value));
        DrawFloat(
            "Base world scale",
            "scale",
            definition.scale,
            value => Set(ref definition.scale, value));
        GUILayout.EndVertical();
    }

    private void DrawPlantingRules()
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Planting and environment");
        DrawToggle(
            "Player property only",
            definition!.playerPropertyOnly,
            value => Set(ref definition.playerPropertyOnly, value));
        DrawToggle(
            "Allow beneath roofs",
            definition.allowRoofed,
            value => Set(ref definition.allowRoofed, value));
        DrawToggle(
            "Grow during winter",
            definition.growsInWinter,
            value => Set(ref definition.growsInWinter, value));
        DrawToggle(
            "Blocks movement",
            definition.blocksMovement,
            value => Set(ref definition.blocksMovement, value));
        DrawToggle(
            "Replace tile plants",
            definition.replaceExistingPlants,
            value => Set(ref definition.replaceExistingPlants, value));
        GUILayout.EndVertical();
    }

    private void DrawStages()
    {
        definition!.stages ??= new List<GrowableStageDefinition>();
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Growth and harvest-ready stages");
        GUILayout.Label(
            "The final stage is harvest-ready. Duration belongs to the " +
            "stage it is shown on and controls advancement to the next one.");

        for (int index = 0; index < definition.stages.Count; index++)
            if (DrawStage(index))
                break;

        if (GUILayout.Button("Add Stage"))
            AddStage();
        GUILayout.EndVertical();
    }

    private bool DrawStage(int index)
    {
        GrowableStageDefinition stage = definition!.stages![index];
        bool finalStage = index == definition.stages.Count - 1;
        int action = 0;

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label(finalStage
            ? $"Stage {index}: harvest-ready"
            : $"Stage {index}");
        GUI.enabled = index > 0;
        if (GUILayout.Button("↑", GUILayout.Width(36)))
            action = -1;
        GUI.enabled = index < definition.stages.Count - 1;
        if (GUILayout.Button("↓", GUILayout.Width(36)))
            action = 1;
        GUI.enabled = definition.stages.Count > 2;
        if (GUILayout.Button("Remove", GUILayout.Width(78)))
            action = 2;
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        string artMode = string.IsNullOrWhiteSpace(stage.image)
            ? string.IsNullOrWhiteSpace(stage.model)
                ? "Base-game sprite"
                : "GLB model"
            : "Custom image";
        DrawChoice(
            "Art source",
            artMode,
            new[] { "Base-game sprite", "Custom image", "GLB model" },
            value =>
            {
                if (value == "Custom image")
                {
                    stage.sprite = null;
                    stage.model = null;
                    stage.image ??= "assets/plant.png";
                }
                else if (value == "GLB model")
                {
                    stage.sprite = null;
                    stage.image = null;
                    stage.model ??= "assets/plant.glb";
                    stage.rotation ??= new[] { 20f, 135f, 0f };
                }
                else
                {
                    stage.image = null;
                    stage.model = null;
                    stage.sprite ??= "sprite_nature_turnip_mature";
                }
                MarkDirty();
            });

        if (!string.IsNullOrWhiteSpace(stage.sprite))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sprite asset", GUILayout.Width(165));
            string sprite = GUILayout.TextField(stage.sprite ?? "");
            if (sprite != stage.sprite)
            {
                stage.sprite = sprite;
                MarkDirty();
            }
            if (GUILayout.Button("Choose… ▼", GUILayout.Width(110)))
                OpenArtworkPicker(stage);
            GUILayout.EndHorizontal();
        }
        else if (!string.IsNullOrWhiteSpace(stage.image))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Image", GUILayout.Width(165));
            string image = GUILayout.TextField(stage.image ?? "");
            if (image != stage.image)
            {
                stage.image = image;
                MarkDirty();
            }
            if (GUILayout.Button("Choose… ▼", GUILayout.Width(110)))
                OpenArtworkPicker(stage);
            if (GUILayout.Button("Import…", GUILayout.Width(90)))
                BrowseArtwork(stage);
            GUILayout.EndHorizontal();
            DrawFloat(
                "Pixels per unit",
                $"stage.{index}.ppu",
                stage.pixelsPerUnit,
                value =>
                {
                    stage.pixelsPerUnit = value;
                    MarkDirty();
                });
            GUILayout.BeginHorizontal();
            GUILayout.Label("Pivot X / Y", GUILayout.Width(165));
            DrawFloatInline(
                $"stage.{index}.pivotX",
                stage.pivotX,
                value =>
                {
                    stage.pivotX = value;
                    MarkDirty();
                });
            DrawFloatInline(
                $"stage.{index}.pivotY",
                stage.pivotY,
                value =>
                {
                    stage.pivotY = value;
                    MarkDirty();
                });
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("GLB model", GUILayout.Width(165));
            string model = GUILayout.TextField(stage.model ?? "");
            if (model != stage.model)
            {
                stage.model = model;
                MarkDirty();
            }
            if (GUILayout.Button("Choose… ▼", GUILayout.Width(110)))
                OpenArtworkPicker(stage);
            if (GUILayout.Button("Import…", GUILayout.Width(90)))
                BrowseArtwork(stage);
            GUILayout.EndHorizontal();

            stage.rotation ??= new[] { 20f, 135f, 0f };
            if (stage.rotation.Length != 3)
                stage.rotation = new[] { 20f, 135f, 0f };
            GUILayout.BeginHorizontal();
            GUILayout.Label("Rotation X / Y / Z", GUILayout.Width(165));
            for (int axis = 0; axis < 3; axis++)
            {
                int captured = axis;
                DrawFloatInline(
                    $"stage.{index}.rotation.{axis}",
                    stage.rotation[axis],
                    value =>
                    {
                        stage.rotation[captured] = value;
                        MarkDirty();
                    });
            }
            GUILayout.EndHorizontal();
            DrawFloat(
                "Zoom",
                $"stage.{index}.zoom",
                stage.zoom,
                value =>
                {
                    stage.zoom = value;
                    MarkDirty();
                });
            DrawInt(
                "Render resolution",
                $"stage.{index}.resolution",
                stage.resolution,
                value =>
                {
                    stage.resolution = value;
                    MarkDirty();
                });
            DrawFloat(
                "Pixels per unit",
                $"stage.{index}.ppu",
                stage.pixelsPerUnit,
                value =>
                {
                    stage.pixelsPerUnit = value;
                    MarkDirty();
                });
            GUILayout.BeginHorizontal();
            GUILayout.Label("Pivot X / Y", GUILayout.Width(165));
            DrawFloatInline(
                $"stage.{index}.pivotX",
                stage.pivotX,
                value =>
                {
                    stage.pivotX = value;
                    MarkDirty();
                });
            DrawFloatInline(
                $"stage.{index}.pivotY",
                stage.pivotY,
                value =>
                {
                    stage.pivotY = value;
                    MarkDirty();
                });
            GUILayout.EndHorizontal();
            GUILayout.Label(
                "The GLB is rendered once from this camera rotation. " +
                "Growables do not generate directional variants.",
                GUI.skin.box);

            GUI.enabled = !renderingGlbPreview;
            if (GUILayout.Button(
                    renderingGlbPreview
                        ? "Rendering GLB preview…"
                        : "Render GLB preview"))
                RenderGlbPreview(stage, index);
            GUI.enabled = true;
            if (glbPreviewStage == index && glbPreview != null)
                DrawStageWorldPreview(glbPreview, stage);
        }

        DrawFloat(
            "Stage scale multiplier",
            $"stage.{index}.scale",
            stage.scale,
            value =>
            {
                stage.scale = value;
                MarkDirty();
            });

        if (!finalStage)
            DrawFloat(
                definition.Clock == GrowableClock.GameTurns
                    ? "Duration (turns)"
                    : "Duration (hours)",
                $"stage.{index}.duration",
                stage.duration,
                value =>
                {
                    stage.duration = value;
                    MarkDirty();
                });
        GUILayout.EndVertical();

        if (action == -1)
            MoveStage(index, index - 1);
        else if (action == 1)
            MoveStage(index, index + 1);
        else if (action == 2)
            RemoveStage(index);
        return action != 0;
    }

    private void DrawHarvest()
    {
        definition!.harvest ??= new List<GrowableHarvestDefinition>();
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Harvest results");
        GUILayout.Label(
            "Use a CIL qualified ID (pack:item) or type a base-game item name.");

        for (int index = 0; index < definition.harvest.Count; index++)
        {
            GrowableHarvestDefinition output = definition.harvest[index];
            bool remove = false;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Result " + (index + 1));
            GUI.enabled = definition.harvest.Count > 1;
            if (GUILayout.Button("Remove", GUILayout.Width(78)))
                remove = true;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            DrawText(
                "Item ID / name",
                output.item ?? "",
                value =>
                {
                    output.item = value;
                    MarkDirty();
                });
            GUILayout.BeginHorizontal();
            GUILayout.Label("Choose item", GUILayout.Width(165));
            if (GUILayout.Button(
                    GetHarvestItemLabel(output) + "  ▼",
                    GUILayout.MinWidth(330),
                    GUILayout.Height(34)))
                OpenHarvestItemPicker(output);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            DrawInt(
                "Minimum",
                $"harvest.{index}.minimum",
                output.minimum,
                value =>
                {
                    output.minimum = value;
                    MarkDirty();
                });
            DrawInt(
                "Maximum",
                $"harvest.{index}.maximum",
                output.maximum,
                value =>
                {
                    output.maximum = value;
                    MarkDirty();
                });
            DrawFloat(
                "Chance (0 to 1)",
                $"harvest.{index}.chance",
                output.chance,
                value =>
                {
                    output.chance = value;
                    MarkDirty();
                });
            GUILayout.EndVertical();
            if (remove)
            {
                definition.harvest.RemoveAt(index);
                numberText.Clear();
                MarkDirty();
                break;
            }
        }

        if (GUILayout.Button("Add Harvest Result"))
        {
            definition.harvest.Add(new GrowableHarvestDefinition
            {
                item = selectedItem?.QualifiedId,
                minimum = 1,
                maximum = 1,
                chance = 1f
            });
            MarkDirty();
        }
        GUILayout.EndVertical();
    }

    private void AddDefinition()
    {
        if (selectedItem == null || SelectedItemInheritsNativeSeeds())
        {
            status =
                "Cannot add growable behavior to an item that inherits " +
                "Silverpine's native seed behavior.";
            return;
        }
        definition = new GrowableDefinition
        {
            visibleName = selectedItem!.DisplayName,
            stages = new List<GrowableStageDefinition>
            {
                new()
                {
                    sprite = "sprite_nature_seeds",
                    duration = 24f
                },
                new()
                {
                    sprite = "sprite_nature_turnip_mature"
                }
            },
            harvest = new List<GrowableHarvestDefinition>
            {
                new()
                {
                    item = selectedItem.QualifiedId,
                    minimum = 1,
                    maximum = 1,
                    chance = 1f
                }
            }
        };
        definition.Clock = GrowableClock.RealTimeHours;
        dirty = true;
        detailsScroll = Vector2.zero;
        status =
            "Growable behavior added. Choose the actual harvest item and art.";
    }

    private int GetHarvestMode()
    {
        if (definition!.perennial)
            return 2;
        return definition.regrowStage >= 0 ? 1 : 0;
    }

    private void SetHarvestMode(int mode)
    {
        mode = Mathf.Clamp(mode, 0, HarvestModes.Length - 1);
        definition!.perennial = mode == 2;
        if (mode == 0)
        {
            definition.regrowStage = -1;
            definition.matureStage = -1;
        }
        else if (mode == 1)
        {
            definition.regrowStage = Math.Max(0, definition.regrowStage);
            definition.matureStage = -1;
        }
        else
        {
            EnsurePerennialStages();
            int mature = Math.Max(0, definition.stages!.Count - 2);
            definition.matureStage = definition.matureStage < 0
                ? mature
                : Math.Min(definition.matureStage, mature);
            definition.regrowStage = Math.Max(
                definition.matureStage,
                definition.regrowStage < 0 ? mature : definition.regrowStage);
            definition.regrowStage = Math.Min(
                definition.regrowStage,
                definition.stages.Count - 2);
        }
        numberText.Clear();
        MarkDirty();
    }

    private void EnsurePerennialStages()
    {
        definition!.stages ??= new List<GrowableStageDefinition>();
        while (definition.stages.Count < 2)
            definition.stages.Add(NewDefaultStage(
                definition.stages.Count == 0
                    ? "sprite_nature_seeds"
                    : "sprite_nature_turnip_mature",
                definition.stages.Count == 0 ? 24f : 0f));
        if (definition.stages.Count >= 3)
            return;

        GrowableStageDefinition ready = definition.stages[1];
        definition.stages.Insert(1, CopyStageArt(ready, 24f));
    }

    private void AddStage()
    {
        definition!.stages ??= new List<GrowableStageDefinition>();
        if (definition.stages.Count == 0)
        {
            definition.stages.Add(NewDefaultStage(
                "sprite_nature_seeds",
                24f));
            definition.stages.Add(NewDefaultStage(
                "sprite_nature_turnip_mature",
                0f));
        }
        else
        {
            GrowableStageDefinition previousFinal =
                definition.stages[definition.stages.Count - 1];
            if (previousFinal.duration <= 0f)
                previousFinal.duration = 24f;
            definition.stages.Add(CopyStageArt(previousFinal, 0f));
        }
        NormalizeStageIndices();
        numberText.Clear();
        MarkDirty();
    }

    private void MoveStage(int from, int to)
    {
        if (definition?.stages == null ||
            from < 0 || from >= definition.stages.Count ||
            to < 0 || to >= definition.stages.Count)
            return;
        GrowableStageDefinition stage = definition.stages[from];
        definition.stages.RemoveAt(from);
        definition.stages.Insert(to, stage);
        definition.regrowStage = RemapMovedIndex(
            definition.regrowStage,
            from,
            to);
        definition.matureStage = RemapMovedIndex(
            definition.matureStage,
            from,
            to);
        NormalizeStageIndices();
        numberText.Clear();
        MarkDirty();
    }

    private void RemoveStage(int index)
    {
        if (definition?.stages == null || definition.stages.Count <= 2)
            return;
        definition.stages.RemoveAt(index);
        if (definition.regrowStage > index)
            definition.regrowStage--;
        if (definition.matureStage > index)
            definition.matureStage--;
        NormalizeStageIndices();
        numberText.Clear();
        MarkDirty();
    }

    private void NormalizeStageIndices()
    {
        if (definition?.stages == null || definition.stages.Count < 2)
            return;
        int lastNonReady = definition.stages.Count - 2;
        if (definition.regrowStage >= 0)
            definition.regrowStage = Mathf.Clamp(
                definition.regrowStage,
                0,
                lastNonReady);
        if (definition.matureStage >= 0)
            definition.matureStage = Mathf.Clamp(
                definition.matureStage,
                0,
                definition.stages.Count - 1);
        if (definition.perennial &&
            definition.matureStage > definition.regrowStage)
            definition.matureStage = definition.regrowStage;
    }

    private string GetHarvestItemLabel(GrowableHarvestDefinition output)
    {
        CustomItemInfo? item = items.FirstOrDefault(value =>
            value.QualifiedId.Equals(
                output.item,
                StringComparison.OrdinalIgnoreCase));
        if (item != null)
            return "CIL • " + item.DisplayName;
        Item? baseItem = ItemLibrary.Items.FirstOrDefault(value =>
            value != null && string.Equals(
                value.name,
                output.item,
                StringComparison.OrdinalIgnoreCase));
        return baseItem == null
            ? "Pick a base-game or CIL item"
            : "Base game • " + baseItem.name;
    }

    private void OpenHarvestItemPicker(GrowableHarvestDefinition output)
    {
        var options = new List<PickerOption>();
        foreach (CustomItemInfo item in items)
            options.Add(new PickerOption
            {
                Value = item.QualifiedId,
                Label = "CIL • " + item.DisplayName + "\n" + item.QualifiedId,
                SearchText = item.DisplayName + " " + item.QualifiedId + " " +
                    item.Category
            });

        var customTemplates = new HashSet<Item>(
            items.Select(value => value.Template));
        foreach (Item item in ItemLibrary.Items
                     .Where(value => value != null &&
                         !customTemplates.Contains(value) &&
                         !string.IsNullOrWhiteSpace(value.name))
                     .GroupBy(value => value.name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderBy(value => value.name, StringComparer.OrdinalIgnoreCase))
            options.Add(new PickerOption
            {
                Value = item.name,
                Label = "Base game • " + item.name,
                SearchText = item.name + " " + item.itemCategory
            });

        OpenPicker(
            "Choose Harvest Item",
            output.item ?? "",
            options,
            value =>
            {
                output.item = value;
                MarkDirty();
            });
    }

    private void OpenArtworkPicker(GrowableStageDefinition stage)
    {
        var options = new List<PickerOption>();
        foreach (string name in GrowableRegistry.GetBaseGameSpriteNames())
            options.Add(new PickerOption
            {
                Value = "sprite|" + name,
                Label = "Base game • " + name,
                SearchText = "base game sprite " + name
            });

        string packFolder = selectedItem == null
            ? ""
            : Path.GetDirectoryName(selectedItem.SourceJsonPath) ?? "";
        if (Directory.Exists(packFolder))
            foreach (string path in Directory.GetFiles(
                         packFolder,
                         "*.*",
                         SearchOption.AllDirectories)
                     .Where(value => HasExtension(
                         value,
                         ".png",
                         ".jpg",
                         ".jpeg",
                         ".glb"))
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string relative = MakeRelativePath(packFolder, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                bool isGlb = HasExtension(path, ".glb");
                options.Add(new PickerOption
                {
                    Value = (isGlb ? "model|" : "image|") + relative,
                    Label = (isGlb ? "Pack GLB • " : "Pack image • ") +
                        relative,
                    SearchText = (isGlb
                        ? "custom pack glb model 3d sprite "
                        : "custom pack image sprite ") + relative
                });
            }

        string current = !string.IsNullOrWhiteSpace(stage.image)
            ? "image|" + stage.image
            : !string.IsNullOrWhiteSpace(stage.model)
                ? "model|" + stage.model
                : "sprite|" + (stage.sprite ?? "");
        OpenPicker(
            "Choose Stage Artwork",
            current,
            options,
            value =>
            {
                if (value.StartsWith("image|", StringComparison.Ordinal))
                {
                    stage.image = value.Substring("image|".Length);
                    stage.sprite = null;
                    stage.model = null;
                }
                else if (value.StartsWith("model|", StringComparison.Ordinal))
                {
                    stage.model = value.Substring("model|".Length);
                    stage.sprite = null;
                    stage.image = null;
                    stage.rotation ??= new[] { 20f, 135f, 0f };
                }
                else if (value.StartsWith("sprite|", StringComparison.Ordinal))
                {
                    stage.sprite = value.Substring("sprite|".Length);
                    stage.image = null;
                    stage.model = null;
                }
                MarkDirty();
            },
            "Import Image / GLB…",
            () => BrowseArtwork(stage));
    }

    private void RefreshItems()
    {
        string previous = selectedItem?.QualifiedId ?? "";
        items.Clear();
        items.AddRange(CustomItemApi.GetRegisteredItems()
            .OrderBy(value => value.PackId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase));
        growableIds.Clear();
        foreach (CustomItemInfo item in items)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(
                        CustomItemApi.GetItemExtensionJson(
                            item.QualifiedId,
                            ExtensionName)))
                    growableIds.Add(item.QualifiedId);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning(
                    $"Could not inspect growable data for " +
                    $"'{item.QualifiedId}': {exception.Message}");
            }
        }

        CustomItemInfo? restored = items.FirstOrDefault(value =>
            value.QualifiedId.Equals(previous, StringComparison.OrdinalIgnoreCase));
        selectedItem = restored ?? items.FirstOrDefault();
        LoadSelected();
        status = items.Count == 0
            ? "No enabled Custom Item Loader items are registered."
            : $"Loaded {items.Count} Custom Item Loader item" +
                (items.Count == 1 ? "." : "s.");
    }

    private void SelectItem(CustomItemInfo item)
    {
        if (dirty || selectedItem?.QualifiedId.Equals(
                item.QualifiedId,
                StringComparison.OrdinalIgnoreCase) == true)
            return;
        selectedItem = item;
        LoadSelected();
    }

    private void LoadSelected()
    {
        ClearGlbPreview();
        definition = null;
        dirty = false;
        numberText.Clear();
        detailsScroll = Vector2.zero;
        if (selectedItem == null)
            return;

        try
        {
            string? json = CustomItemApi.GetItemExtensionJson(
                selectedItem.QualifiedId,
                ExtensionName);
            if (!string.IsNullOrWhiteSpace(json))
            {
                definition = JsonConvert.DeserializeObject<GrowableDefinition>(json!);
                if (definition != null &&
                    Enum.TryParse(
                        definition.clock,
                        ignoreCase: true,
                        out GrowableClock parsed))
                    definition.Clock = parsed;
            }
            status = "Loaded " + selectedItem.QualifiedId;
        }
        catch (Exception exception)
        {
            status = "Could not load growable data: " + exception.Message;
            Plugin.Log.LogError(
                $"Growables editor could not load '{selectedItem.QualifiedId}': " +
                exception);
        }
    }

    private void SaveSelected()
    {
        if (selectedItem == null)
            return;
        try
        {
            string? json = null;
            if (definition != null)
            {
                if (SelectedItemInheritsNativeSeeds())
                    throw new InvalidDataException(
                        "Growable behavior cannot be attached to an item " +
                        "that inherits Silverpine's native " +
                        "ItemComponent_Seeds. Use a component-free clone or " +
                        "custom artwork.");
                string candidate = JsonConvert.SerializeObject(
                    definition,
                    Formatting.None,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });
                GrowableDefinition validation =
                    JsonConvert.DeserializeObject<GrowableDefinition>(candidate) ??
                    throw new InvalidDataException(
                        "The growable definition could not be validated.");
                validation.Validate(
                    selectedItem.SourceJsonPath,
                    selectedItem.QualifiedId);
                json = candidate;
            }

            CustomItemApi.SetItemExtensionJson(
                Plugin.PluginGuid,
                selectedItem.QualifiedId,
                ExtensionName,
                json);
            if (definition == null)
                growableIds.Remove(selectedItem.QualifiedId);
            else
                growableIds.Add(selectedItem.QualifiedId);
            dirty = false;
            status =
                "Saved through Custom Item Loader. Restart Silverpine to " +
                "apply registration changes.";
        }
        catch (Exception exception)
        {
            status = "Save failed: " + exception.Message;
            Plugin.Log.LogError(
                $"Growables editor could not save '{selectedItem.QualifiedId}': " +
                exception);
        }
    }

    private async void RenderGlbPreview(
        GrowableStageDefinition stage,
        int stageIndex)
    {
        if (selectedItem == null || string.IsNullOrWhiteSpace(stage.model))
        {
            status = "Choose or import a GLB model first.";
            return;
        }

        string selectedId = selectedItem.QualifiedId;
        renderingGlbPreview = true;
        status = "Rendering one-direction GLB preview…";
        try
        {
            string folder = Path.GetFullPath(
                Path.GetDirectoryName(selectedItem.SourceJsonPath)!);
            string modelPath = Path.GetFullPath(
                Path.Combine(folder, stage.model!));
            string folderPrefix = folder.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!modelPath.StartsWith(
                    folderPrefix,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The GLB model must stay inside the item pack folder.");
            if (!HasExtension(modelPath, ".glb"))
                throw new InvalidDataException("The model must be a .glb file.");
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(
                    "The GLB model does not exist.",
                    modelPath);

            Sprite rendered = await CustomItemApi.RenderGlbSpriteAsync(
                modelPath,
                stage.ModelRotation,
                stage.zoom,
                stage.resolution,
                "custom_growable_editor_preview");
            if (!open || selectedItem == null ||
                !selectedItem.QualifiedId.Equals(
                    selectedId,
                    StringComparison.OrdinalIgnoreCase))
            {
                DestroySpriteAndTexture(rendered);
                return;
            }

            ClearGlbPreview();
            glbPreview = rendered;
            glbPreviewStage = stageIndex;
            status = "Rendered one-direction GLB preview.";
        }
        catch (Exception exception)
        {
            status = "GLB preview failed: " + exception.Message;
            Plugin.Log.LogError(
                "Growables editor GLB preview failed: " + exception);
        }
        finally
        {
            renderingGlbPreview = false;
        }
    }

    private void DrawStageWorldPreview(
        Sprite sprite,
        GrowableStageDefinition stage)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("In-game size comparison");
        Rect rect = GUILayoutUtility.GetRect(
            280f,
            520f,
            300f,
            300f,
            GUILayout.ExpandWidth(true));
        Color old = GUI.color;
        GUI.color = new Color(0.11f, 0.16f, 0.13f, 1f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;

        float baseScale = definition == null ||
            float.IsNaN(definition.scale) ||
            float.IsInfinity(definition.scale) ||
            definition.scale <= 0f
                ? 1f
                : definition.scale;
        float stageScale = float.IsNaN(stage.scale) ||
            float.IsInfinity(stage.scale) || stage.scale <= 0f
                ? 1f
                : stage.scale;
        float effectiveScale = baseScale * stageScale;
        float pixelsPerUnit = float.IsNaN(stage.pixelsPerUnit) ||
            float.IsInfinity(stage.pixelsPerUnit) ||
            stage.pixelsPerUnit <= 0f
                ? 32f
                : stage.pixelsPerUnit;
        float worldWidth = sprite.rect.width / pixelsPerUnit * effectiveScale;
        float worldHeight = sprite.rect.height / pixelsPerUnit * effectiveScale;
        float pivotX = Mathf.Clamp01(stage.pivotX);
        float pivotY = Mathf.Clamp01(stage.pivotY);

        float minimumX = Mathf.Min(-0.5f, -pivotX * worldWidth);
        float maximumX = Mathf.Max(0.5f, (1f - pivotX) * worldWidth);
        float minimumY = Mathf.Min(-0.5f, -pivotY * worldHeight);
        float maximumY = Mathf.Max(0.5f, (1f - pivotY) * worldHeight);
        Rect inner = new(
            rect.x + 16f,
            rect.y + 16f,
            rect.width - 32f,
            rect.height - 32f);
        float tileSize = Mathf.Min(
            TerrainTileScreenSize,
            inner.width / Mathf.Max(1f, maximumX - minimumX),
            inner.height / Mathf.Max(1f, maximumY - minimumY));
        float centerX = (minimumX + maximumX) * 0.5f;
        float centerY = (minimumY + maximumY) * 0.5f;
        Vector2 origin = new(
            inner.center.x - centerX * tileSize,
            inner.center.y + centerY * tileSize);

        Rect tileRect = new(
            origin.x - tileSize * 0.5f,
            origin.y - tileSize * 0.5f,
            tileSize,
            tileSize);
        DrawGrassTerrainTile(tileRect);
        Rect spriteRect = new(
            origin.x - pivotX * worldWidth * tileSize,
            origin.y - (1f - pivotY) * worldHeight * tileSize,
            worldWidth * tileSize,
            worldHeight * tileSize);
        GUI.color = new Color(0f, 0f, 0f, 0.38f);
        GUI.DrawTexture(
            new Rect(
                origin.x - Mathf.Min(tileSize * 0.4f, spriteRect.width * 0.32f),
                origin.y - 3f,
                Mathf.Min(tileSize * 0.8f, spriteRect.width * 0.64f),
                Mathf.Max(4f, Mathf.Min(tileSize * 0.08f, spriteRect.height * 0.08f))),
            Texture2D.whiteTexture);
        GUI.color = Color.white;
        DrawSpriteTexture(sprite, spriteRect);
        GUI.color = old;

        GUILayout.Label(
            $"Plant: {worldWidth:0.###} × {worldHeight:0.###} tiles; " +
            "grass turf: 1 × 1 tile.");
        GUILayout.Label(
            $"Effective scale: {baseScale:0.###} base × " +
            $"{stageScale:0.###} stage = {effectiveScale:0.###}×.");
        GUILayout.EndVertical();
    }

    private void DrawGrassTerrainTile(Rect rect)
    {
        Color old = GUI.color;
        GUI.color = new Color(0.04f, 0.055f, 0.045f, 0.9f);
        GUI.DrawTexture(
            new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f),
            Texture2D.whiteTexture);
        GUI.color = Color.white;

        Sprite? grass = GetGrassTerrainSprite();
        if (grass != null)
        {
            DrawSpriteTexture(grass, rect);
            GUI.color = old;
            return;
        }

        GUI.color = new Color(0.22f, 0.39f, 0.18f, 1f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
    }

    private Sprite? GetGrassTerrainSprite()
    {
        if (grassTerrainSprite != null)
            return grassTerrainSprite;
        if (grassTerrainLookupAttempted)
            return null;

        grassTerrainLookupAttempted = true;
        foreach (SpriteRenderer renderer in
                 Resources.FindObjectsOfTypeAll<SpriteRenderer>())
        {
            if (renderer == null || renderer.sprite == null)
                continue;
            string objectName = renderer.gameObject.name;
            if (!objectName.Equals(
                    "prefab_tile_grass",
                    StringComparison.OrdinalIgnoreCase) &&
                !objectName.StartsWith(
                    "prefab_tile_grass (",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            grassTerrainSprite = renderer.sprite;
            Plugin.Log.LogInfo(
                $"Growables editor is using terrain sprite " +
                $"'{grassTerrainSprite.name}' as its one-tile size reference.");
            return grassTerrainSprite;
        }

        foreach (Sprite candidate in Resources.FindObjectsOfTypeAll<Sprite>())
            if (candidate.name.Equals(
                    "grass",
                    StringComparison.OrdinalIgnoreCase))
            {
                grassTerrainSprite = candidate;
                return grassTerrainSprite;
            }

        Plugin.Log.LogWarning(
            "Growables editor could not locate prefab_tile_grass; using a " +
            "built-in one-tile grass reference.");
        return null;
    }

    private static void DrawSpriteTexture(Sprite sprite, Rect destination)
    {
        Rect source = sprite.textureRect;
        Texture texture = sprite.texture;
        Rect coordinates = new(
            source.x / texture.width,
            source.y / texture.height,
            source.width / texture.width,
            source.height / texture.height);
        GUI.DrawTextureWithTexCoords(
            destination,
            texture,
            coordinates,
            alphaBlend: true);
    }

    private void BrowseArtwork(GrowableStageDefinition stage)
    {
        if (selectedItem == null)
            return;
        filePickerOpen = true;
        try
        {
            FilePickerUI.GetFileSelection(
                path => HasExtension(
                    path,
                    ".png",
                    ".jpg",
                    ".jpeg",
                    ".glb"),
                selected =>
                {
                    filePickerOpen = false;
                    if (selected == "Exit")
                        return;
                    try
                    {
                        string folder = Path.GetDirectoryName(
                            selectedItem.SourceJsonPath)!;
                        string assets = Path.Combine(folder, "assets");
                        Directory.CreateDirectory(assets);
                        string destination = Path.Combine(
                            assets,
                            Path.GetFileName(selected));
                        if (!Path.GetFullPath(selected).Equals(
                                Path.GetFullPath(destination),
                                StringComparison.OrdinalIgnoreCase))
                            File.Copy(selected, destination, overwrite: true);
                        string relative = MakeRelativePath(folder, destination)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        if (HasExtension(destination, ".glb"))
                        {
                            stage.model = relative;
                            stage.image = null;
                            stage.sprite = null;
                            stage.rotation ??= new[] { 20f, 135f, 0f };
                            status = "Imported GLB model as " + stage.model;
                        }
                        else
                        {
                            stage.image = relative;
                            stage.model = null;
                            stage.sprite = null;
                            status = "Imported growable art as " + stage.image;
                        }
                        MarkDirty();
                    }
                    catch (Exception exception)
                    {
                        status = "Artwork import failed: " + exception.Message;
                        Plugin.Log.LogError(
                            "Growables editor artwork import failed: " + exception);
                    }
                });

            if (GenericListUI.Instance == null ||
                !GenericListUI.Instance.gameObject.activeInHierarchy)
                filePickerOpen = false;
        }
        catch
        {
            filePickerOpen = false;
            throw;
        }
    }

    private bool MatchesSearch(CustomItemInfo item)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.QualifiedId.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private bool SelectedItemInheritsNativeSeeds() =>
        selectedItem != null && InheritsNativeSeeds(selectedItem);

    private static bool InheritsNativeSeeds(CustomItemInfo item) =>
        item.Template.TryGetItemComponent<ItemComponent_Seeds>(out _);

    private void DrawText(
        string label,
        string value,
        Action<string> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(165));
        string next = GUILayout.TextField(value ?? "");
        if (next != value)
            assign(next);
        GUILayout.EndHorizontal();
    }

    private void DrawToggle(
        string label,
        bool value,
        Action<bool> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(165));
        bool next = GUILayout.Toggle(value, value ? "Enabled" : "Disabled");
        if (next != value)
            assign(next);
        GUILayout.EndHorizontal();
    }

    private void DrawInt(
        string label,
        string key,
        int value,
        Action<int> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(165));
        string next = GUILayout.TextField(NumberText(
            key,
            value.ToString(CultureInfo.InvariantCulture)));
        numberText[key] = next;
        if (int.TryParse(
                next,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed) && parsed != value)
            assign(parsed);
        GUILayout.EndHorizontal();
    }

    private void DrawFloat(
        string label,
        string key,
        float value,
        Action<float> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(165));
        DrawFloatInline(key, value, assign);
        GUILayout.EndHorizontal();
    }

    private void DrawFloatInline(
        string key,
        float value,
        Action<float> assign)
    {
        string next = GUILayout.TextField(
            NumberText(
                key,
                value.ToString("0.###", CultureInfo.InvariantCulture)),
            GUILayout.MinWidth(60));
        numberText[key] = next;
        if (float.TryParse(
                next,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed) && !Mathf.Approximately(parsed, value))
            assign(parsed);
    }

    private void DrawChoice(
        string label,
        string current,
        IReadOnlyList<string> values,
        Action<string> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(165));
        int index = IndexOf(values, current);
        if (GUILayout.Button(
                values[index] + "  ▼",
                GUILayout.Width(285),
                GUILayout.Height(30)))
            OpenPicker(
                "Choose " + label,
                values[index],
                values.Select(value => new PickerOption
                {
                    Value = value,
                    Label = value,
                    SearchText = value
                }),
                assign);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawStageIndex(
        string label,
        int current,
        int maximum,
        Action<int> assign)
    {
        maximum = Math.Max(0, maximum);
        current = Mathf.Clamp(current, 0, maximum);
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(165));
        if (GUILayout.Button(
                GetStagePickerLabel(current) + "  ▼",
                GUILayout.Width(285),
                GUILayout.Height(30)))
        {
            var options = new List<PickerOption>();
            for (int index = 0; index <= maximum; index++)
            {
                string value = index.ToString(CultureInfo.InvariantCulture);
                string stageLabel = GetStagePickerLabel(index);
                options.Add(new PickerOption
                {
                    Value = value,
                    Label = stageLabel,
                    SearchText = stageLabel
                });
            }
            OpenPicker(
                "Choose " + label,
                current.ToString(CultureInfo.InvariantCulture),
                options,
                value =>
                {
                    if (int.TryParse(
                            value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int parsed))
                        assign(parsed);
                });
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private string GetStagePickerLabel(int index)
    {
        string suffix = "";
        if (definition?.stages != null &&
            index >= 0 && index < definition.stages.Count)
        {
            GrowableStageDefinition stage = definition.stages[index];
            string art = !string.IsNullOrWhiteSpace(stage.sprite)
                ? stage.sprite!
                : !string.IsNullOrWhiteSpace(stage.image)
                    ? Path.GetFileName(stage.image)
                    : Path.GetFileName(stage.model ?? "GLB model");
            suffix = " — " + art;
            if (index == definition.stages.Count - 1)
                suffix += " (harvest-ready)";
        }
        return "Stage " + index + suffix;
    }

    private void OpenPicker(
        string title,
        string current,
        IEnumerable<PickerOption> options,
        Action<string> callback,
        string? actionLabel = null,
        Action? action = null)
    {
        pickerOptions.Clear();
        pickerOptions.AddRange(options);
        pickerTitle = title;
        pickerCurrent = current ?? "";
        pickerSearch = "";
        pickerScroll = Vector2.zero;
        pickerCallback = callback;
        pickerActionLabel = actionLabel ?? "";
        pickerAction = action;
        pickerOpen = true;
    }

    private void DrawPickerScreen()
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(pickerTitle, GUILayout.Width(410));
        GUILayout.Label(pickerOptions.Count + " choices");
        if (!string.IsNullOrWhiteSpace(pickerActionLabel) &&
            GUILayout.Button(pickerActionLabel, GUILayout.Width(190)))
        {
            Action? action = pickerAction;
            ClosePicker();
            action?.Invoke();
            GUILayout.EndHorizontal();
            return;
        }
        if (GUILayout.Button("Cancel", GUILayout.Width(110)))
        {
            ClosePicker();
            GUILayout.EndHorizontal();
            return;
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(
            "Search by name, ID, category, or asset name. Escape returns to " +
            "the Growables Editor without changing the value.");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(72));
        pickerSearch = GUILayout.TextField(
            pickerSearch ?? "",
            GUILayout.Height(32));
        if (GUILayout.Button("Clear", GUILayout.Width(80)))
            pickerSearch = "";
        GUILayout.EndHorizontal();

        pickerScroll = GUILayout.BeginScrollView(pickerScroll, GUI.skin.box);
        int shown = 0;
        foreach (PickerOption option in pickerOptions)
        {
            if (!PickerMatches(option))
                continue;
            shown++;
            Color old = GUI.backgroundColor;
            if (option.Value.Equals(
                    pickerCurrent,
                    StringComparison.OrdinalIgnoreCase))
                GUI.backgroundColor = new Color(0.48f, 0.75f, 0.46f);
            if (GUILayout.Button(option.Label, GUILayout.MinHeight(42)))
            {
                Action<string>? callback = pickerCallback;
                string value = option.Value;
                ClosePicker();
                callback?.Invoke(value);
                GUI.backgroundColor = old;
                break;
            }
            GUI.backgroundColor = old;
        }
        if (shown == 0)
            GUILayout.Label("No matching choices.");
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private bool PickerMatches(PickerOption option)
    {
        if (string.IsNullOrWhiteSpace(pickerSearch))
            return true;
        return option.Label.Contains(
                pickerSearch,
                StringComparison.OrdinalIgnoreCase) ||
            option.Value.Contains(
                pickerSearch,
                StringComparison.OrdinalIgnoreCase) ||
            option.SearchText.Contains(
                pickerSearch,
                StringComparison.OrdinalIgnoreCase);
    }

    private void ClosePicker()
    {
        pickerOpen = false;
        pickerCallback = null;
        pickerAction = null;
        pickerOptions.Clear();
        pickerTitle = "";
        pickerCurrent = "";
        pickerSearch = "";
        pickerActionLabel = "";
        pickerScroll = Vector2.zero;
    }

    private string NumberText(string key, string fallback)
    {
        if (!numberText.TryGetValue(key, out string value))
        {
            value = fallback;
            numberText.Add(key, value);
        }
        return value;
    }

    private void Set(ref string? field, string value)
    {
        if (field == value)
            return;
        field = value;
        MarkDirty();
    }

    private void Set(ref int field, int value)
    {
        if (field == value)
            return;
        field = value;
        MarkDirty();
    }

    private void Set(ref float field, float value)
    {
        if (Mathf.Approximately(field, value))
            return;
        field = value;
        MarkDirty();
    }

    private void Set(ref bool field, bool value)
    {
        if (field == value)
            return;
        field = value;
        MarkDirty();
    }

    private new void MarkDirty() => dirty = true;

    private void Close()
    {
        ClearGlbPreview();
        filePickerOpen = false;
        ClosePicker();
        open = false;
        dirty = false;
        numberText.Clear();
        items.Clear();
        growableIds.Clear();
        selectedItem = null;
        definition = null;
        gameObject.SetActive(false);
        ReleaseSession();
    }

    private static GrowableStageDefinition NewDefaultStage(
        string sprite,
        float duration) => new()
    {
        sprite = sprite,
        duration = duration,
        pixelsPerUnit = 32f,
        pivotX = 0.5f,
        pivotY = 0.5f
    };

    private static GrowableStageDefinition CopyStageArt(
        GrowableStageDefinition source,
        float duration) => new()
    {
        sprite = source.sprite,
        image = source.image,
        model = source.model,
        rotation = source.rotation?.ToArray(),
        zoom = source.zoom,
        resolution = source.resolution,
        scale = source.scale,
        duration = duration,
        pixelsPerUnit = source.pixelsPerUnit,
        pivotX = source.pivotX,
        pivotY = source.pivotY
    };

    private void ClearGlbPreview()
    {
        if (glbPreview != null)
            DestroySpriteAndTexture(glbPreview);
        glbPreview = null;
        glbPreviewStage = -1;
    }

    private static void DestroySpriteAndTexture(Sprite sprite)
    {
        Texture2D texture = sprite.texture;
        Destroy(sprite);
        if (texture != null)
            Destroy(texture);
    }

    private static int RemapMovedIndex(int value, int from, int to)
    {
        if (value < 0)
            return value;
        if (value == from)
            return to;
        if (from < to && value > from && value <= to)
            return value - 1;
        if (to < from && value >= to && value < from)
            return value + 1;
        return value;
    }

    private static int IndexOf(
        IReadOnlyList<string> values,
        string current)
    {
        for (int index = 0; index < values.Count; index++)
            if (values[index].Equals(
                    current,
                    StringComparison.OrdinalIgnoreCase))
                return index;
        return 0;
    }

    private static string MakeRelativePath(string folder, string path)
    {
        Uri root = new(
            Path.GetFullPath(folder).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar);
        Uri target = new(Path.GetFullPath(path));
        return Uri.UnescapeDataString(root.MakeRelativeUri(target).ToString());
    }

    private static bool HasExtension(
        string path,
        params string[] extensions) =>
        extensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
