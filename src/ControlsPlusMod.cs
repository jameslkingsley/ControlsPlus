using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.UI;
using HarmonyLib;
using StationeersMods.Interface;
using UnityEngine;

namespace ControlsPlus;

[StationeersMod(ModId, DisplayName, Version)]
public sealed class ControlsPlusMod : ModBehaviour
{
    public const string ModId = "com.james.controlsplus";
    public const string DisplayName = "Controls Plus";
    public const string Version = "0.4.0";

    private const string ControlsGroupName = "Controls Plus";
    private const string SensorLensesControl = "Toggle Sensor Lenses";
    private const string TabletControl = "Toggle Tablet";
    private const string ConstructControl = "Construct";
    private const string DeconstructControl = "Deconstruct";
    private const string ItemHotkeyPreferencesPrefix = "ControlsPlus.ItemHotkeys";

    private static readonly KeyCode[] ItemHotkeyKeys =
    {
        KeyCode.Alpha1,
        KeyCode.Alpha2,
        KeyCode.Alpha3,
        KeyCode.Alpha4,
        KeyCode.Alpha5,
        KeyCode.Alpha6,
        KeyCode.Alpha7,
        KeyCode.Alpha8,
        KeyCode.Alpha9,
        KeyCode.Alpha0
    };

    private static readonly MethodInfo AddKeyMethod = typeof(KeyManager).GetMethod(
        "AddKey",
        BindingFlags.Static | BindingFlags.NonPublic,
        null,
        new[] { typeof(string), typeof(KeyCode), typeof(ControlsGroup), typeof(bool) },
        null);

    private static ControlsPlusMod _instance;

    private KeyCode _sensorLensesKey = KeyCode.F7;
    private KeyCode _tabletKey = KeyCode.F8;
    private KeyCode _constructKey = KeyCode.F9;
    private KeyCode _deconstructKey = KeyCode.F10;
    private Coroutine _equipmentRoutine;
    private readonly Dictionary<long, Slot> _originalSlots = new Dictionary<long, Slot>();
    private readonly HashSet<long> _activeSmartStows = new HashSet<long>();
    private readonly long[] _itemHotkeyReferences = new long[ItemHotkeyKeys.Length];
    private readonly Dictionary<int, HotkeyDisplacement> _hotkeyDisplacements =
        new Dictionary<int, HotkeyDisplacement>();
    private readonly HashSet<int> _activeItemHotkeys = new HashSet<int>();
    private Harmony _harmony;
    private bool _bypassEnhancedSmartStow;
    private string _itemHotkeyScope = string.Empty;

    public override void OnLoaded(ContentHandler contentHandler)
    {
        base.OnLoaded(contentHandler);

        try
        {
            _instance = this;
            _harmony = new Harmony(ModId);
            _harmony.PatchAll(typeof(ControlsPlusMod).Assembly);
            RegisterControls();
            KeyManager.OnControlsChanged += RefreshBindings;
            RefreshBindings();
            LogMessage(
                $"Loaded. Sensor lenses: {_sensorLensesKey}; tablet: {_tabletKey}; " +
                $"construct: {_constructKey}; deconstruct: {_deconstructKey}; " +
                "enhanced Smart Stow enabled.");

            if (IsAssemblyLoaded("InventoryTweaks"))
            {
                LogMessage(
                    "Inventory Tweaks is also loaded and patches Smart Stow. " +
                    "Remove it to avoid conflicting inventory behavior.");
            }
        }
        catch (Exception exception)
        {
            LogException("Failed to register controls", exception);
        }
    }

    private void Update()
    {
        if (!ControlsShouldFunction())
        {
            return;
        }

        EnsureItemHotkeysLoaded();

        if (TryHandleItemHotkeyInput())
        {
            return;
        }

        if (_sensorLensesKey != KeyCode.None && KeyManager.GetButtonDown(_sensorLensesKey))
        {
            ToggleSensorLenses();
        }

        if (_tabletKey != KeyCode.None && KeyManager.GetButtonDown(_tabletKey))
        {
            ToggleTablet();
        }

        if (_constructKey != KeyCode.None && KeyManager.GetButtonDown(_constructKey))
        {
            EquipConstructionRequirements(false);
        }

        if (_deconstructKey != KeyCode.None && KeyManager.GetButtonDown(_deconstructKey))
        {
            EquipConstructionRequirements(true);
        }
    }

    private void OnDestroy()
    {
        KeyManager.OnControlsChanged -= RefreshBindings;
        _harmony?.UnpatchSelf();
        _originalSlots.Clear();
        _activeSmartStows.Clear();
        Array.Clear(_itemHotkeyReferences, 0, _itemHotkeyReferences.Length);
        _hotkeyDisplacements.Clear();
        _activeItemHotkeys.Clear();
        _itemHotkeyScope = string.Empty;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private static void RegisterControls()
    {
        if (AddKeyMethod == null)
        {
            throw new MissingMethodException(typeof(KeyManager).FullName, "AddKey");
        }

        ControlsGroup group = KeyManager.GetControlsGroup(ControlsGroupName);
        if (group == null)
        {
            group = new ControlsGroup(ControlsGroupName);
            KeyManager.AddGroupLookup(group);
        }

        AddControlIfMissing(SensorLensesControl, KeyCode.F7, group);
        AddControlIfMissing(TabletControl, KeyCode.F8, group);
        AddControlIfMissing(ConstructControl, KeyCode.F9, group);
        AddControlIfMissing(DeconstructControl, KeyCode.F10, group);
        ControlsAssignment.RefreshState();
    }

    private static void AddControlIfMissing(string name, KeyCode defaultKey, ControlsGroup group)
    {
        if (KeyManager.GetKeyitem(name) == null)
        {
            AddKeyMethod.Invoke(null, new object[] { name, defaultKey, group, false });
        }
    }

    private void RefreshBindings()
    {
        _sensorLensesKey = KeyManager.GetKey(SensorLensesControl);
        _tabletKey = KeyManager.GetKey(TabletControl);
        _constructKey = KeyManager.GetKey(ConstructControl);
        _deconstructKey = KeyManager.GetKey(DeconstructControl);
    }

    private static bool ControlsShouldFunction()
    {
        if (GameManager.GameState != GameState.Running || WorldManager.IsGamePaused || ConsoleWindow.IsOpen)
        {
            return false;
        }

        if (InputWindow.InputState != InputPanelState.None ||
            InputPrefabs.InputState != InputPanelState.None ||
            InputSourceCode.InputState != InputPanelState.None)
        {
            return false;
        }

        Human player = InventoryManager.ParentHuman;
        return player != null && player.IsLocalPlayer;
    }

    private bool TryHandleItemHotkeyInput()
    {
        for (int index = 0; index < ItemHotkeyKeys.Length; index++)
        {
            if (!KeyManager.GetButtonDown(ItemHotkeyKeys[index]))
            {
                continue;
            }

            if (InputMouse.IsMouseControl)
            {
                Slot hoveredSlot = SlotDisplayButton.CurrentSlot?.Slot;
                Item hoveredItem = hoveredSlot?.Get<Item>();
                if (hoveredItem != null && IsInLocalPlayerInventory(hoveredItem))
                {
                    ToggleItemHotkeyAssignment(index, hoveredItem);
                    return true;
                }

                return false;
            }

            if (_itemHotkeyReferences[index] == 0L)
            {
                return false;
            }

            ToggleHotkeyItem(index);
            return true;
        }

        return false;
    }

    private void ToggleItemHotkeyAssignment(int hotkeyIndex, Item item)
    {
        long referenceId = item.ReferenceId;
        if (_itemHotkeyReferences[hotkeyIndex] == referenceId)
        {
            _itemHotkeyReferences[hotkeyIndex] = 0L;
            SaveItemHotkeys();
            return;
        }

        for (int index = 0; index < _itemHotkeyReferences.Length; index++)
        {
            if (_itemHotkeyReferences[index] == referenceId)
            {
                _itemHotkeyReferences[index] = 0L;
            }
        }

        _itemHotkeyReferences[hotkeyIndex] = referenceId;
        SaveItemHotkeys();
    }

    private void ToggleHotkeyItem(int hotkeyIndex)
    {
        if (_activeItemHotkeys.Contains(hotkeyIndex))
        {
            return;
        }

        long referenceId = _itemHotkeyReferences[hotkeyIndex];
        Item item = Thing.Find<Item>(referenceId);
        if (item == null || item.IsBeingDestroyed)
        {
            _itemHotkeyReferences[hotkeyIndex] = 0L;
            SaveItemHotkeys();
            return;
        }

        if (!IsInLocalPlayerInventory(item) || item.ParentSlot == null)
        {
            return;
        }

        if (item.ParentSlot.IsHandSlot)
        {
            if (_hotkeyDisplacements.TryGetValue(hotkeyIndex, out HotkeyDisplacement displacement))
            {
                Slot handSlot = item.ParentSlot;
                Slot stowSlot = FindPreservingStowSlot(item);
                if (stowSlot != null)
                {
                    StartCoroutine(StowHotkeyAndRestoreDisplaced(
                        hotkeyIndex,
                        item,
                        handSlot,
                        stowSlot,
                        displacement));
                }

                return;
            }

            InventoryManager.SmartStow(item.ParentSlot);
            return;
        }

        Slot targetHand = FindFreeHand();
        if (targetHand == null)
        {
            if (TryFindDisplaceableHand(
                    out targetHand,
                    out Item displacedItem,
                    out Slot displacedStowSlot) &&
                CanMoveTo(item, targetHand))
            {
                StartCoroutine(TemporarilyStowAndEquipHotkey(
                    hotkeyIndex,
                    item,
                    targetHand,
                    displacedItem,
                    displacedStowSlot));
            }

            return;
        }

        if (!CanStoreInSlot(item, targetHand))
        {
            return;
        }

        _originalSlots[item.ReferenceId] = item.ParentSlot;
        Human player = InventoryManager.ParentHuman;
        if (!ReferenceEquals(targetHand, InventoryManager.ActiveHandSlot))
        {
            player?.SwapHands();
        }

        OnServer.MoveToSlot(item, targetHand);
    }

    private bool TryFindDisplaceableHand(
        out Slot handSlot,
        out Item displacedItem,
        out Slot stowSlot)
    {
        Human player = InventoryManager.ParentHuman;
        Slot activeHand = InventoryManager.ActiveHandSlot;
        Slot otherHand = ReferenceEquals(activeHand, player?.LeftHandSlot)
            ? player?.RightHandSlot
            : player?.LeftHandSlot;

        if (TryGetHandDisplacement(activeHand, out displacedItem, out stowSlot))
        {
            handSlot = activeHand;
            return true;
        }

        if (TryGetHandDisplacement(otherHand, out displacedItem, out stowSlot))
        {
            handSlot = otherHand;
            return true;
        }

        handSlot = null;
        displacedItem = null;
        stowSlot = null;
        return false;
    }

    private bool TryGetHandDisplacement(
        Slot handSlot,
        out Item displacedItem,
        out Slot stowSlot)
    {
        displacedItem = handSlot?.Get<Item>();
        stowSlot = displacedItem == null ? null : FindPreservingStowSlot(displacedItem);
        return displacedItem != null && stowSlot != null;
    }

    private Slot FindPreservingStowSlot(DynamicThing item)
    {
        if (TryGetOriginalSlot(item, out Slot originalSlot) &&
            originalSlot.Get() == null &&
            CanStoreInSlot(item, originalSlot))
        {
            return originalSlot;
        }

        Human player = InventoryManager.ParentHuman;
        if (player == null)
        {
            return null;
        }

        foreach (Slot slot in EnumerateSlots(player, new HashSet<long>()))
        {
            if (slot == null ||
                slot.IsHandSlot ||
                slot.Get() != null ||
                IsSlotInsideThing(slot, item) ||
                !CanStoreInSlot(item, slot))
            {
                continue;
            }

            return slot;
        }

        return null;
    }

    private static bool IsSlotInsideThing(Slot slot, DynamicThing item)
    {
        DynamicThing current = slot?.Parent as DynamicThing;
        while (current != null)
        {
            if (ReferenceEquals(current, item))
            {
                return true;
            }

            current = current.ParentSlot?.Parent as DynamicThing;
        }

        return false;
    }

    private IEnumerator TemporarilyStowAndEquipHotkey(
        int hotkeyIndex,
        Item hotkeyItem,
        Slot handSlot,
        Item displacedItem,
        Slot displacedStowSlot)
    {
        _activeItemHotkeys.Add(hotkeyIndex);
        Slot hotkeyOrigin = hotkeyItem.ParentSlot;
        try
        {
            OnServer.MoveToSlot(displacedItem, displacedStowSlot);
            yield return WaitForItemSlot(displacedItem, displacedStowSlot);
            if (!ReferenceEquals(displacedItem.ParentSlot, displacedStowSlot) || handSlot.Get() != null)
            {
                yield break;
            }

            _originalSlots[hotkeyItem.ReferenceId] = hotkeyOrigin;
            _hotkeyDisplacements[hotkeyIndex] = new HotkeyDisplacement(
                displacedItem.ReferenceId);

            Human player = InventoryManager.ParentHuman;
            if (!ReferenceEquals(handSlot, InventoryManager.ActiveHandSlot))
            {
                player?.SwapHands();
            }

            OnServer.MoveToSlot(hotkeyItem, handSlot);
            yield return WaitForItemSlot(hotkeyItem, handSlot);
            if (!ReferenceEquals(hotkeyItem.ParentSlot, handSlot))
            {
                _hotkeyDisplacements.Remove(hotkeyIndex);
                if (handSlot.Get() == null && CanStoreInSlot(displacedItem, handSlot))
                {
                    OnServer.MoveToSlot(displacedItem, handSlot);
                }
            }
        }
        finally
        {
            _activeItemHotkeys.Remove(hotkeyIndex);
        }
    }

    private IEnumerator StowHotkeyAndRestoreDisplaced(
        int hotkeyIndex,
        Item hotkeyItem,
        Slot handSlot,
        Slot hotkeyStowSlot,
        HotkeyDisplacement displacement)
    {
        _activeItemHotkeys.Add(hotkeyIndex);
        try
        {
            OnServer.MoveToSlot(hotkeyItem, hotkeyStowSlot);
            yield return WaitForItemSlot(hotkeyItem, hotkeyStowSlot);
            if (!ReferenceEquals(hotkeyItem.ParentSlot, hotkeyStowSlot) || handSlot.Get() != null)
            {
                yield break;
            }

            Item displacedItem = Thing.Find<Item>(displacement.ItemReferenceId);
            if (displacedItem == null ||
                displacedItem.IsBeingDestroyed ||
                !IsInLocalPlayerInventory(displacedItem) ||
                !CanStoreInSlot(displacedItem, handSlot))
            {
                _hotkeyDisplacements.Remove(hotkeyIndex);
                yield break;
            }

            OnServer.MoveToSlot(displacedItem, handSlot);
            yield return WaitForItemSlot(displacedItem, handSlot);
            if (ReferenceEquals(displacedItem.ParentSlot, handSlot))
            {
                _hotkeyDisplacements.Remove(hotkeyIndex);
            }
        }
        finally
        {
            _activeItemHotkeys.Remove(hotkeyIndex);
        }
    }

    private static IEnumerator WaitForItemSlot(DynamicThing item, Slot expectedSlot)
    {
        float deadline = Time.realtimeSinceStartup + 1.5f;
        while (item != null &&
               !item.IsBeingDestroyed &&
               !ReferenceEquals(item.ParentSlot, expectedSlot) &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }
    }

    private static Slot FindFreeHand()
    {
        Human player = InventoryManager.ParentHuman;
        Slot activeHand = InventoryManager.ActiveHandSlot;
        if (activeHand?.Get() == null)
        {
            return activeHand;
        }

        Slot otherHand = ReferenceEquals(activeHand, player?.LeftHandSlot)
            ? player?.RightHandSlot
            : player?.LeftHandSlot;
        return otherHand?.Get() == null ? otherHand : null;
    }

    private static bool IsInLocalPlayerInventory(DynamicThing item)
    {
        Human player = InventoryManager.ParentHuman;
        try
        {
            return item != null &&
                   item.ParentSlot != null &&
                   player != null &&
                   item.RootParent == player.RootParent;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void EnsureItemHotkeysLoaded()
    {
        string scope = GetItemHotkeyScope();
        if (string.IsNullOrEmpty(scope) || string.Equals(scope, _itemHotkeyScope, StringComparison.Ordinal))
        {
            return;
        }

        Array.Clear(_itemHotkeyReferences, 0, _itemHotkeyReferences.Length);
        _hotkeyDisplacements.Clear();
        _activeItemHotkeys.Clear();
        _itemHotkeyScope = scope;

        for (int index = 0; index < _itemHotkeyReferences.Length; index++)
        {
            string serialized = PlayerPrefs.GetString(GetItemHotkeyPreferenceKey(index), "0");
            if (long.TryParse(
                    serialized,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long referenceId) &&
                referenceId > 0L)
            {
                _itemHotkeyReferences[index] = referenceId;
            }
        }

    }

    private void SaveItemHotkeys()
    {
        if (string.IsNullOrEmpty(_itemHotkeyScope))
        {
            return;
        }

        for (int index = 0; index < _itemHotkeyReferences.Length; index++)
        {
            PlayerPrefs.SetString(
                GetItemHotkeyPreferenceKey(index),
                _itemHotkeyReferences[index].ToString(CultureInfo.InvariantCulture));
        }

        PlayerPrefs.Save();
    }

    private static string GetItemHotkeyScope()
    {
        Human player = InventoryManager.ParentHuman;
        if (player == null || string.IsNullOrWhiteSpace(World.CurrentId))
        {
            return string.Empty;
        }

        return $"{World.CurrentId}.{player.ReferenceId.ToString(CultureInfo.InvariantCulture)}";
    }

    private string GetItemHotkeyPreferenceKey(int hotkeyIndex)
    {
        return $"{ItemHotkeyPreferencesPrefix}.{_itemHotkeyScope}.{hotkeyIndex}";
    }

    private bool ShouldSuppressVanillaEquipmentHotkey(string buttonName)
    {
        if (InputMouse.IsMouseControl || string.IsNullOrEmpty(buttonName))
        {
            return false;
        }

        KeyCode equipmentKey = KeyManager.GetKey(buttonName);
        for (int index = 0; index < ItemHotkeyKeys.Length; index++)
        {
            if (_itemHotkeyReferences[index] != 0L &&
                ItemHotkeyKeys[index] == equipmentKey &&
                (Input.GetKey(equipmentKey) || Input.GetKeyUp(equipmentKey)))
            {
                return true;
            }
        }

        return false;
    }

    private static void ToggleSensorLenses()
    {
        Human player = InventoryManager.ParentHuman;
        DynamicThing lenses = player?.GlassesSlot?.Get();
        Interactable toggle = lenses?.InteractOnOff;

        if (toggle != null)
        {
            toggle.PlayerInteractWith();
        }
        else
        {
            PlayFailureSound();
        }
    }

    private static void ToggleTablet()
    {
        Human player = InventoryManager.ParentHuman;
        if (InventoryManager.Instance == null || player == null)
        {
            return;
        }

        Slot tabletSlot = FindTabletSlot(player);
        if (tabletSlot == null)
        {
            PlayFailureSound();
            return;
        }

        InventoryManager.SmartStow(tabletSlot);
    }

    private static Slot FindTabletSlot(Human player)
    {
        if (player.LeftHandSlot?.Get<Tablet>() != null)
        {
            return player.LeftHandSlot;
        }

        if (player.RightHandSlot?.Get<Tablet>() != null)
        {
            return player.RightHandSlot;
        }

        HashSet<long> visited = new HashSet<long>();
        foreach (Slot slot in EnumerateSlots(player, visited))
        {
            if (slot.Get<Tablet>() != null)
            {
                return slot;
            }
        }

        return null;
    }

    private bool TryHandleEnhancedSmartStow(Slot sourceSlot)
    {
        if (_bypassEnhancedSmartStow || sourceSlot == null)
        {
            return false;
        }

        DynamicThing selectedThing = sourceSlot.Get();
        if (selectedThing == null || !sourceSlot.IsHandSlot)
        {
            return false;
        }

        long referenceId = selectedThing.ReferenceId;
        if (_activeSmartStows.Contains(referenceId))
        {
            return true;
        }

        if (TryGetOriginalSlot(selectedThing, out Slot originalSlot) &&
            originalSlot.Get() == null &&
            CanStoreInSlot(selectedThing, originalSlot))
        {
            OnServer.MoveToSlot(selectedThing, originalSlot);
            _originalSlots.Remove(referenceId);
            return true;
        }

        if (selectedThing is Stackable sourceStack)
        {
            List<Stackable> targets = FindCompatibleStackTargets(sourceStack, originalSlot);
            if (targets.Count > 0)
            {
                _activeSmartStows.Add(referenceId);
                StartCoroutine(MergeStacksThenStow(sourceStack, targets));
                return true;
            }
        }

        return false;
    }

    private List<Stackable> FindCompatibleStackTargets(Stackable source, Slot originalSlot)
    {
        List<Stackable> targets = new List<Stackable>();
        HashSet<long> seen = new HashSet<long>();

        AddCompatibleStackTarget(targets, seen, source, originalSlot?.Get<Stackable>());

        Human player = InventoryManager.ParentHuman;
        if (player == null)
        {
            return targets;
        }

        foreach (Slot slot in EnumerateSlots(player, new HashSet<long>()))
        {
            if (slot == null || slot.IsHandSlot)
            {
                continue;
            }

            AddCompatibleStackTarget(targets, seen, source, slot.Get<Stackable>());
        }

        return targets;
    }

    private static void AddCompatibleStackTarget(
        ICollection<Stackable> targets,
        ISet<long> seen,
        Stackable source,
        Stackable target)
    {
        if (!IsCompatibleStack(source, target) || !seen.Add(target.ReferenceId))
        {
            return;
        }

        targets.Add(target);
    }

    private static bool IsCompatibleStack(Stackable source, Stackable target)
    {
        try
        {
            return source != null &&
                   target != null &&
                   source.ReferenceId != target.ReferenceId &&
                   !target.IsStackFull &&
                   source.CanStack(target) &&
                   source.ColorState == target.ColorState &&
                   source.IsColorCompatible(target);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IEnumerator MergeStacksThenStow(Stackable source, IReadOnlyList<Stackable> targets)
    {
        long referenceId = source.ReferenceId;
        try
        {
            foreach (Stackable target in targets)
            {
                if (!IsHeldUsableStack(source) || !IsCompatibleStack(source, target))
                {
                    continue;
                }

                int sourceQuantity = source.Quantity;
                int targetQuantity = target.Quantity;
                Thing.Merge(target, source);

                float deadline = Time.realtimeSinceStartup + 1.5f;
                while (IsHeldUsableStack(source) &&
                       source.Quantity >= sourceQuantity &&
                       target != null &&
                       target.Quantity <= targetQuantity &&
                       Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }
            }

            if (IsHeldUsableStack(source))
            {
                Slot sourceSlot = source.ParentSlot;
                if (TryGetOriginalSlot(source, out Slot originalSlot) &&
                    originalSlot.Get() == null &&
                    CanStoreInSlot(source, originalSlot))
                {
                    OnServer.MoveToSlot(source, originalSlot);
                }
                else
                {
                    _bypassEnhancedSmartStow = true;
                    try
                    {
                        InventoryManager.SmartStow(sourceSlot);
                    }
                    finally
                    {
                        _bypassEnhancedSmartStow = false;
                    }
                }
            }
        }
        finally
        {
            _activeSmartStows.Remove(referenceId);
            _originalSlots.Remove(referenceId);
        }
    }

    private static bool IsHeldUsableStack(Stackable stack)
    {
        return stack != null &&
               !stack.IsBeingDestroyed &&
               stack.Quantity > 0 &&
               stack.ParentSlot != null &&
               stack.ParentSlot.IsHandSlot;
    }

    private bool TryGetOriginalSlot(DynamicThing item, out Slot originalSlot)
    {
        originalSlot = null;
        if (item == null || !_originalSlots.TryGetValue(item.ReferenceId, out Slot storedSlot))
        {
            return false;
        }

        Human player = InventoryManager.ParentHuman;
        try
        {
            if (storedSlot == null ||
                storedSlot.Parent == null ||
                storedSlot.IsHandSlot ||
                player == null ||
                storedSlot.Parent.RootParent != player.RootParent)
            {
                _originalSlots.Remove(item.ReferenceId);
                return false;
            }
        }
        catch (Exception)
        {
            _originalSlots.Remove(item.ReferenceId);
            return false;
        }

        originalSlot = storedSlot;
        return true;
    }

    private void RecordInventoryTransition(DynamicThing item, Slot source, Slot destination)
    {
        if (item == null || destination == null)
        {
            return;
        }

        if (destination.IsHandSlot && source != null && !source.IsHandSlot)
        {
            _originalSlots[item.ReferenceId] = source;
        }
        else if (!destination.IsHandSlot && source != null && source.IsHandSlot)
        {
            _originalSlots.Remove(item.ReferenceId);
        }
    }

    private static bool CanStoreInSlot(DynamicThing item, Slot slot)
    {
        try
        {
            return item != null && slot != null && Slot.AllowMove(item, slot);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAssemblyLoaded(string assemblyName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(
                    assembly.GetName().Name,
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void EquipConstructionRequirements(bool deconstruct)
    {
        if (_equipmentRoutine != null)
        {
            return;
        }

        Structure structure = FindLookedAtStructure();
        ToolUse toolUse = deconstruct
            ? structure?.CurrentBuildState?.Tool
            : structure?.NextBuildState?.Tool;
        if (toolUse == null)
        {
            return;
        }

        List<EquipmentRequirement> requirements = deconstruct
            ? GetDeconstructionRequirements(toolUse)
            : GetConstructionRequirements(toolUse);
        EquipmentPlan plan = CreateEquipmentPlan(
            InventoryManager.ParentHuman,
            requirements,
            out string failureReason);
        if (plan == null)
        {
            LogMessage(
                $"{(deconstruct ? "Deconstruct" : "Construct")} no-op for " +
                $"{structure.PrefabName} at build state {structure.CurrentBuildStateIndex}: " +
                failureReason);
            return;
        }

        if (plan.Steps.Count == 0)
        {
            return;
        }

        _equipmentRoutine = StartCoroutine(ExecuteEquipmentPlan(plan));
    }

    private static Structure FindLookedAtStructure()
    {
        // Use the game's resolved interaction target. Some structures swap to
        // colliders that are not directly registered with Thing.Find at lower
        // build states, while CursorManager has already resolved their owner.
        return CursorManager.CursorThing?.AsStructure;
    }

    private static List<EquipmentRequirement> GetConstructionRequirements(ToolUse toolUse)
    {
        List<EquipmentRequirement> requirements = new List<EquipmentRequirement>(2);
        if (toolUse.ToolEntry != null)
        {
            requirements.Add(new EquipmentRequirement(
                item => toolUse.IsToolEntry(item),
                Math.Max(1, toolUse.EntryQuantity)));
        }

        if (toolUse.ToolEntry2 != null)
        {
            requirements.Add(new EquipmentRequirement(
                item => toolUse.IsToolEntry2(item),
                Math.Max(1, toolUse.EntryQuantity2)));
        }

        return requirements;
    }

    private static List<EquipmentRequirement> GetDeconstructionRequirements(ToolUse toolUse)
    {
        List<EquipmentRequirement> requirements = new List<EquipmentRequirement>(1);
        if (toolUse.ToolExit != null)
        {
            requirements.Add(new EquipmentRequirement(
                item => toolUse.IsToolExit(item),
                Math.Max(1, toolUse.ExitQuantity)));
        }

        return requirements;
    }

    private static EquipmentPlan CreateEquipmentPlan(
        Human player,
        IReadOnlyList<EquipmentRequirement> requirements,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (player == null || requirements == null || requirements.Count == 0 || requirements.Count > 2)
        {
            failureReason = "the build state has no supported equipment requirements";
            return null;
        }

        Slot primaryHand = InventoryManager.ActiveHandSlot ?? player.LeftHandSlot;
        Slot secondaryHand = ReferenceEquals(primaryHand, player.LeftHandSlot)
            ? player.RightHandSlot
            : player.LeftHandSlot;
        if (primaryHand == null || (requirements.Count > 1 && secondaryHand == null))
        {
            failureReason = "the required player hand is unavailable";
            return null;
        }

        List<Slot> allSlots = new List<Slot>(EnumerateSlots(player, new HashSet<long>()));
        Slot[] targetHands = requirements.Count == 1
            ? new[]
            {
                SelectSingleRequirementHand(
                    requirements[0],
                    primaryHand,
                    secondaryHand)
            }
            : new[] { primaryHand, secondaryHand };
        Item[] selectedItems = new Item[requirements.Count];
        HashSet<long> selectedIds = new HashSet<long>();

        for (int index = 0; index < requirements.Count; index++)
        {
            selectedItems[index] = FindRequirementItem(
                requirements[index],
                targetHands[index],
                primaryHand,
                secondaryHand,
                allSlots,
                selectedIds);
            if (selectedItems[index] == null)
            {
                failureReason = $"no accessible inventory item matches requirement {index + 1}";
                return null;
            }

            selectedIds.Add(selectedItems[index].ReferenceId);
        }

        EquipmentPlan plan = FindEquipmentPlan(
            targetHands,
            selectedItems,
            allSlots,
            new[] { primaryHand, secondaryHand });
        if (plan == null)
        {
            failureReason = "no compatible move or swap route could equip the selected items";
        }

        return plan;
    }

    private static Slot SelectSingleRequirementHand(
        EquipmentRequirement requirement,
        Slot primaryHand,
        Slot secondaryHand)
    {
        HashSet<long> noExcludedItems = new HashSet<long>();
        if (IsRequirementCandidate(primaryHand?.Get<Item>(), requirement, noExcludedItems))
        {
            return primaryHand;
        }

        if (IsRequirementCandidate(secondaryHand?.Get<Item>(), requirement, noExcludedItems))
        {
            return secondaryHand;
        }

        if (primaryHand?.Get() == null)
        {
            return primaryHand;
        }

        if (secondaryHand?.Get() == null)
        {
            return secondaryHand;
        }

        return primaryHand;
    }

    private static Item FindRequirementItem(
        EquipmentRequirement requirement,
        Slot preferredHand,
        Slot primaryHand,
        Slot secondaryHand,
        IEnumerable<Slot> allSlots,
        HashSet<long> excludedIds)
    {
        Item preferred = preferredHand?.Get<Item>();
        if (IsRequirementCandidate(preferred, requirement, excludedIds))
        {
            return preferred;
        }

        Slot otherHand = ReferenceEquals(preferredHand, primaryHand) ? secondaryHand : primaryHand;
        Item other = otherHand?.Get<Item>();
        if (IsRequirementCandidate(other, requirement, excludedIds))
        {
            return other;
        }

        foreach (Slot slot in allSlots)
        {
            Item candidate = slot?.Get<Item>();
            if (IsRequirementCandidate(candidate, requirement, excludedIds))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsRequirementCandidate(
        Item item,
        EquipmentRequirement requirement,
        HashSet<long> excludedIds)
    {
        if (item == null ||
            excludedIds.Contains(item.ReferenceId) ||
            item.IsBeingDestroyed ||
            item.IsBeingDragged ||
            (item is Stackable && item.GetQuantity < requirement.Quantity))
        {
            return false;
        }

        try
        {
            return requirement.Matches(item);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static EquipmentPlan FindEquipmentPlan(
        IReadOnlyList<Slot> targetHands,
        IReadOnlyList<Item> selectedItems,
        IReadOnlyList<Slot> allSlots,
        IReadOnlyList<Slot> availableHands)
    {
        List<Slot> trackedSlots = new List<Slot>();
        AddDistinctSlots(trackedSlots, availableHands);
        foreach (Item selectedItem in selectedItems)
        {
            AddDistinctSlot(trackedSlots, selectedItem.ParentSlot);
        }

        // Empty slots are useful escape valves, but selected-item source slots
        // are tracked even when occupied so a direct swap can work with a full
        // inventory.
        foreach (Slot slot in allSlots)
        {
            if (slot?.Get() == null)
            {
                AddDistinctSlot(trackedSlots, slot);
            }
        }

        List<Item> trackedItems = new List<Item>();
        foreach (Slot slot in trackedSlots)
        {
            AddDistinctItem(trackedItems, slot.Get<Item>());
        }

        int[] initialState = new int[trackedSlots.Count];
        for (int slotIndex = 0; slotIndex < trackedSlots.Count; slotIndex++)
        {
            initialState[slotIndex] = IndexOfItem(trackedItems, trackedSlots[slotIndex].Get<Item>());
        }

        int[] targetSlotIndexes = new int[targetHands.Count];
        int[] desiredItemIndexes = new int[selectedItems.Count];
        for (int index = 0; index < targetHands.Count; index++)
        {
            targetSlotIndexes[index] = IndexOfSlot(trackedSlots, targetHands[index]);
            desiredItemIndexes[index] = IndexOfItem(trackedItems, selectedItems[index]);
        }

        int[] availableHandIndexes = new int[availableHands.Count];
        for (int index = 0; index < availableHands.Count; index++)
        {
            availableHandIndexes[index] = IndexOfSlot(trackedSlots, availableHands[index]);
        }

        List<PlannedSlotOperation> operations = new List<PlannedSlotOperation>();
        HashSet<string> visited = new HashSet<string>();
        if (!SearchEquipmentPlan(
                initialState,
                trackedSlots,
                trackedItems,
                targetSlotIndexes,
                desiredItemIndexes,
                availableHandIndexes,
                operations,
                visited,
                0))
        {
            return null;
        }

        EquipmentPlan plan = new EquipmentPlan();
        int[] replayState = (int[])initialState.Clone();
        foreach (PlannedSlotOperation operation in operations)
        {
            int sourceItemIndex = replayState[operation.SourceIndex];
            int destinationItemIndex = replayState[operation.DestinationIndex];
            Slot sourceSlot = trackedSlots[operation.SourceIndex];
            Slot destinationSlot = trackedSlots[operation.DestinationIndex];

            if (destinationItemIndex < 0)
            {
                plan.Steps.Add(MoveStep.Move(trackedItems[sourceItemIndex], destinationSlot));
            }
            else
            {
                // After the swap, the old destination occupant is in source,
                // and the old source occupant is in destination.
                plan.Steps.Add(MoveStep.Swap(
                    sourceSlot,
                    destinationSlot,
                    trackedItems[destinationItemIndex],
                    trackedItems[sourceItemIndex]));
            }

            replayState[operation.SourceIndex] = destinationItemIndex;
            replayState[operation.DestinationIndex] = sourceItemIndex;
        }

        return plan;
    }

    private static bool SearchEquipmentPlan(
        int[] state,
        IReadOnlyList<Slot> slots,
        IReadOnlyList<Item> items,
        IReadOnlyList<int> targetSlotIndexes,
        IReadOnlyList<int> desiredItemIndexes,
        IReadOnlyList<int> availableHandIndexes,
        List<PlannedSlotOperation> operations,
        HashSet<string> visited,
        int depth)
    {
        if (EquipmentGoalReached(state, targetSlotIndexes, desiredItemIndexes))
        {
            return true;
        }

        if (depth >= 6 || !visited.Add(string.Join(",", state)))
        {
            return false;
        }

        List<PlannedSlotOperation> candidates = new List<PlannedSlotOperation>();

        // Try goal-producing moves and swaps first.
        for (int goalIndex = 0; goalIndex < targetSlotIndexes.Count; goalIndex++)
        {
            int targetIndex = targetSlotIndexes[goalIndex];
            int desiredIndex = desiredItemIndexes[goalIndex];
            if (state[targetIndex] == desiredIndex)
            {
                continue;
            }

            int sourceIndex = Array.IndexOf(state, desiredIndex);
            AddOperationIfValid(candidates, state, slots, items, sourceIndex, targetIndex);

            // A spare hand can bridge an otherwise incompatible swap, such as
            // moving a sheet out of the active hand while replacing a tool in
            // a tool-belt-only slot.
            foreach (int handIndex in availableHandIndexes)
            {
                AddOperationIfValid(candidates, state, slots, items, sourceIndex, handIndex);
            }
        }

        // If a direct swap is incompatible, temporarily move a hand occupant
        // into any compatible empty slot and retry on the next search level.
        foreach (int targetIndex in targetSlotIndexes)
        {
            if (state[targetIndex] < 0)
            {
                continue;
            }

            for (int destinationIndex = 0; destinationIndex < slots.Count; destinationIndex++)
            {
                if (state[destinationIndex] < 0)
                {
                    AddOperationIfValid(
                        candidates,
                        state,
                        slots,
                        items,
                        targetIndex,
                        destinationIndex);
                }
            }
        }

        foreach (PlannedSlotOperation candidate in candidates)
        {
            int[] nextState = (int[])state.Clone();
            int displaced = nextState[candidate.DestinationIndex];
            nextState[candidate.DestinationIndex] = nextState[candidate.SourceIndex];
            nextState[candidate.SourceIndex] = displaced;
            operations.Add(candidate);

            if (SearchEquipmentPlan(
                    nextState,
                    slots,
                    items,
                    targetSlotIndexes,
                    desiredItemIndexes,
                    availableHandIndexes,
                    operations,
                    visited,
                    depth + 1))
            {
                return true;
            }

            operations.RemoveAt(operations.Count - 1);
        }

        return false;
    }

    private static void AddOperationIfValid(
        ICollection<PlannedSlotOperation> operations,
        IReadOnlyList<int> state,
        IReadOnlyList<Slot> slots,
        IReadOnlyList<Item> items,
        int sourceIndex,
        int destinationIndex)
    {
        if (sourceIndex < 0 || destinationIndex < 0 || sourceIndex == destinationIndex)
        {
            return;
        }

        int sourceItemIndex = state[sourceIndex];
        int destinationItemIndex = state[destinationIndex];
        if (sourceItemIndex < 0 || !CanMoveTo(items[sourceItemIndex], slots[destinationIndex]))
        {
            return;
        }

        if (destinationItemIndex >= 0 && !CanMoveTo(items[destinationItemIndex], slots[sourceIndex]))
        {
            return;
        }

        foreach (PlannedSlotOperation existing in operations)
        {
            if (existing.SourceIndex == sourceIndex && existing.DestinationIndex == destinationIndex)
            {
                return;
            }
        }

        operations.Add(new PlannedSlotOperation(sourceIndex, destinationIndex));
    }

    private static bool CanMoveTo(Item item, Slot slot)
    {
        try
        {
            // Slot.AllowMove also requires the live destination to be empty,
            // which is incorrect while evaluating a simulated swap. Check the
            // invariant compatibility rules here; occupancy is represented by
            // the planner's state and executed later via MoveToSlot/SwapSlots.
            return item != null &&
                   slot != null &&
                   !slot.IsLocked &&
                   item.CanEnter(slot) &&
                   slot.IsAllowedType(item);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool EquipmentGoalReached(
        IReadOnlyList<int> state,
        IReadOnlyList<int> targetSlotIndexes,
        IReadOnlyList<int> desiredItemIndexes)
    {
        for (int index = 0; index < targetSlotIndexes.Count; index++)
        {
            if (state[targetSlotIndexes[index]] != desiredItemIndexes[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void AddDistinctSlots(List<Slot> destination, IEnumerable<Slot> slots)
    {
        foreach (Slot slot in slots)
        {
            AddDistinctSlot(destination, slot);
        }
    }

    private static void AddDistinctSlot(List<Slot> destination, Slot slot)
    {
        if (slot != null && IndexOfSlot(destination, slot) < 0)
        {
            destination.Add(slot);
        }
    }

    private static int IndexOfSlot(IReadOnlyList<Slot> slots, Slot target)
    {
        for (int index = 0; index < slots.Count; index++)
        {
            if (ReferenceEquals(slots[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private static void AddDistinctItem(List<Item> destination, Item item)
    {
        if (item != null && IndexOfItem(destination, item) < 0)
        {
            destination.Add(item);
        }
    }

    private static int IndexOfItem(IReadOnlyList<Item> items, Item target)
    {
        if (target == null)
        {
            return -1;
        }

        for (int index = 0; index < items.Count; index++)
        {
            if (items[index]?.ReferenceId == target.ReferenceId)
            {
                return index;
            }
        }

        return -1;
    }

    private IEnumerator ExecuteEquipmentPlan(EquipmentPlan plan)
    {
        foreach (MoveStep step in plan.Steps)
        {
            if (step.IsSwap)
            {
                if (!TrySwap(step.FirstSlot, step.SecondSlot))
                {
                    _equipmentRoutine = null;
                    yield break;
                }

                float swapDeadline = Time.realtimeSinceStartup + 1.5f;
                while ((!ReferenceEquals(step.FirstExpected.ParentSlot, step.FirstSlot) ||
                        !ReferenceEquals(step.SecondExpected.ParentSlot, step.SecondSlot)) &&
                       Time.realtimeSinceStartup < swapDeadline)
                {
                    yield return null;
                }

                if (!ReferenceEquals(step.FirstExpected.ParentSlot, step.FirstSlot) ||
                    !ReferenceEquals(step.SecondExpected.ParentSlot, step.SecondSlot))
                {
                    _equipmentRoutine = null;
                    yield break;
                }

                continue;
            }

            if (ReferenceEquals(step.Item.ParentSlot, step.TargetSlot))
            {
                continue;
            }

            if (step.TargetSlot.Get() != null || !TryMove(step.Item, step.TargetSlot))
            {
                _equipmentRoutine = null;
                yield break;
            }

            float moveDeadline = Time.realtimeSinceStartup + 1.5f;
            while (!ReferenceEquals(step.Item.ParentSlot, step.TargetSlot) &&
                   Time.realtimeSinceStartup < moveDeadline)
            {
                yield return null;
            }

            if (!ReferenceEquals(step.Item.ParentSlot, step.TargetSlot))
            {
                _equipmentRoutine = null;
                yield break;
            }
        }

        _equipmentRoutine = null;
    }

    private static bool TryMove(DynamicThing item, Slot target)
    {
        try
        {
            OnServer.MoveToSlot(item, target);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TrySwap(Slot first, Slot second)
    {
        try
        {
            OnServer.SwapSlots(first, second);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<Slot> EnumerateSlots(Thing thing, HashSet<long> visited)
    {
        if (thing == null || !visited.Add(thing.ReferenceId) || thing.Slots == null)
        {
            yield break;
        }

        foreach (Slot slot in thing.Slots)
        {
            if (slot == null || !slot.IsInteractable)
            {
                continue;
            }

            yield return slot;

            DynamicThing occupant = slot.Get();
            foreach (Slot nestedSlot in EnumerateSlots(occupant, visited))
            {
                yield return nestedSlot;
            }
        }
    }

    private static void PlayFailureSound()
    {
        UIAudioManager.Play(UIAudioManager.ActionFailHash);
    }

    private static void LogMessage(string message)
    {
        Debug.unityLogger.Log(LogType.Log, $"[Controls Plus] {message}");
    }

    private static void LogException(string message, Exception exception)
    {
        Debug.unityLogger.Log(LogType.Error, $"[Controls Plus] {message}: {exception}");
    }

    [HarmonyPatch]
    private static class SmartStowPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(InventoryManager),
                nameof(InventoryManager.SmartStow),
                new[] { typeof(Slot) });
        }

        [HarmonyPrefix]
        private static bool Prefix(Slot __0)
        {
            try
            {
                return _instance == null || !_instance.TryHandleEnhancedSmartStow(__0);
            }
            catch (Exception exception)
            {
                LogException("Enhanced Smart Stow failed; falling back to vanilla", exception);
                return true;
            }
        }
    }

    [HarmonyPatch]
    private static class EquipmentHotkeyConflictPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(InventoryManager),
                "CheckDisplaySlot",
                new[] { typeof(SlotDisplay), typeof(string) });
        }

        [HarmonyPrefix]
        private static bool Prefix(string __1, ref bool __result)
        {
            if (_instance == null || !_instance.ShouldSuppressVanillaEquipmentHotkey(__1))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    private static class MoveToSlotTrackingPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(OnServer),
                nameof(OnServer.MoveToSlot),
                new[] { typeof(DynamicThing), typeof(Slot) });
        }

        [HarmonyPrefix]
        private static void Prefix(DynamicThing __0, Slot __1)
        {
            _instance?.RecordInventoryTransition(__0, __0?.ParentSlot, __1);
        }
    }

    [HarmonyPatch]
    private static class SwapSlotsTrackingPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(OnServer),
                nameof(OnServer.SwapSlots),
                new[] { typeof(Slot), typeof(Slot) });
        }

        [HarmonyPrefix]
        private static void Prefix(Slot __0, Slot __1)
        {
            if (_instance == null || __0 == null || __1 == null)
            {
                return;
            }

            DynamicThing firstOccupant = __0.Get();
            DynamicThing secondOccupant = __1.Get();
            _instance.RecordInventoryTransition(firstOccupant, __0, __1);
            _instance.RecordInventoryTransition(secondOccupant, __1, __0);
        }
    }

    private sealed class EquipmentRequirement
    {
        public EquipmentRequirement(Func<Item, bool> matches, int quantity)
        {
            Matches = matches;
            Quantity = quantity;
        }

        public Func<Item, bool> Matches { get; }

        public int Quantity { get; }
    }

    private sealed class EquipmentPlan
    {
        public List<MoveStep> Steps { get; } = new List<MoveStep>();
    }

    private sealed class HotkeyDisplacement
    {
        public HotkeyDisplacement(long itemReferenceId)
        {
            ItemReferenceId = itemReferenceId;
        }

        public long ItemReferenceId { get; }
    }

    private sealed class PlannedSlotOperation
    {
        public PlannedSlotOperation(int sourceIndex, int destinationIndex)
        {
            SourceIndex = sourceIndex;
            DestinationIndex = destinationIndex;
        }

        public int SourceIndex { get; }

        public int DestinationIndex { get; }
    }

    private sealed class MoveStep
    {
        private MoveStep()
        {
        }

        public bool IsSwap { get; private set; }

        public DynamicThing Item { get; private set; }

        public Slot TargetSlot { get; private set; }

        public Slot FirstSlot { get; private set; }

        public Slot SecondSlot { get; private set; }

        public DynamicThing FirstExpected { get; private set; }

        public DynamicThing SecondExpected { get; private set; }

        public static MoveStep Move(DynamicThing item, Slot target)
        {
            return new MoveStep
            {
                Item = item,
                TargetSlot = target
            };
        }

        public static MoveStep Swap(
            Slot first,
            Slot second,
            DynamicThing firstExpected,
            DynamicThing secondExpected)
        {
            return new MoveStep
            {
                IsSwap = true,
                FirstSlot = first,
                SecondSlot = second,
                FirstExpected = firstExpected,
                SecondExpected = secondExpected
            };
        }
    }
}
