using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

/// <summary>
/// Provides slot + item browsing over the game's Item sheet so the user can
/// pick which piece of gear the imported outfit should replace.
/// </summary>
public sealed class GameDataService
{
    private readonly IDataManager _data;
    private readonly IPluginLog   _log;

    /// <summary>Searchable equipment/accessory items (model-backed only), by slot.</summary>
    private Dictionary<EquipSlot, List<GameItem>>? _itemsBySlot;
    private Dictionary<uint, GameItem>?            _itemsByRowId;

    public GameDataService(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    /// <summary>A searchable game item from the Item excel sheet.</summary>
    public sealed class GameItem
    {
        public uint      RowId        { get; init; }
        public string    Name         { get; init; } = string.Empty;
        public ushort    ModelId      { get; init; }
        public ushort    Variant      { get; init; }
        public EquipSlot Slot         { get; init; }
        public bool      IsAccessory  { get; init; }

        public string ModelIdPadded  => ModelId.ToString("D4");
        public string ModelIdDisplay => $"{ModelIdPadded}-{Variant}";
    }

    /// <summary>All items for a slot, sorted by name. Triggers a cache build if needed.</summary>
    public List<GameItem> GetAllItemsForSlot(EquipSlot slot)
        => GetItemCache().TryGetValue(slot, out var list) ? list : new();

    /// <summary>Looks an item up by row ID (used to restore the last selection).</summary>
    public GameItem? GetItemByRowId(uint rowId)
    {
        GetItemCache();
        return _itemsByRowId != null && _itemsByRowId.TryGetValue(rowId, out var item)
            ? item
            : null;
    }

    /// <summary>Total number of cached items.</summary>
    public int CacheCount
        => GetItemCache().Values.Sum(l => l.Count);

    // ─────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    private Dictionary<EquipSlot, List<GameItem>> GetItemCache()
    {
        if (_itemsBySlot != null) return _itemsBySlot;

        var bySlot   = new Dictionary<EquipSlot, List<GameItem>>();
        var byRowId  = new Dictionary<uint, GameItem>();

        try
        {
            var sheet = _data.GetExcelSheet<Item>();
            if (sheet == null) goto done;

            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (row.ModelMain == 0) continue;

                var primaryId = (ushort)(row.ModelMain & 0xFFFF);
                if (primaryId == 0) continue;

                if (!TryGetEquipSlot(row, out var slot, out var isAcc)) continue;

                var variant = (ushort)((row.ModelMain >> 16) & 0xFFFF);

                var item = new GameItem
                {
                    RowId       = row.RowId,
                    Name        = name,
                    ModelId     = primaryId,
                    Variant     = variant,
                    Slot        = slot,
                    IsAccessory = isAcc,
                };

                if (!bySlot.TryGetValue(slot, out var list))
                {
                    list = new List<GameItem>();
                    bySlot[slot] = list;
                }
                list.Add(item);
                byRowId[item.RowId] = item;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[XPS] Failed to build item cache");
        }

        done:
        foreach (var list in bySlot.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _itemsBySlot = bySlot;
        _itemsByRowId = byRowId;
        _log.Information("[XPS] Item cache built: {0} equipment/accessory items", CacheCount);
        return _itemsBySlot;
    }

    /// <summary>
    /// Inspects the item's <c>EquipSlotCategory</c> sub-row to determine which
    /// equipment slot it occupies. Returns false for weapons, offhands and anything
    /// outside the ten slots we support.
    /// </summary>
    private static bool TryGetEquipSlot(Item row, out EquipSlot slot, out bool isAcc)
    {
        slot  = EquipSlot.Body;
        isAcc = false;

        try
        {
            var catRowId = row.EquipSlotCategory.RowId;
            if (catRowId == 0) return false;

            var cat = row.EquipSlotCategory.Value;

            // Equipment (prefix 'e')
            if (cat.Head   != 0) { slot = EquipSlot.Head;      return true; }
            if (cat.Body   != 0) { slot = EquipSlot.Body;      return true; }
            if (cat.Gloves != 0) { slot = EquipSlot.Hands;     return true; }
            if (cat.Legs   != 0) { slot = EquipSlot.Legs;      return true; }
            if (cat.Feet   != 0) { slot = EquipSlot.Feet;      return true; }

            // Accessories (prefix 'a')
            if (cat.Ears     != 0) { slot = EquipSlot.Earring;   isAcc = true; return true; }
            if (cat.Neck     != 0) { slot = EquipSlot.Neck;      isAcc = true; return true; }
            if (cat.Wrists   != 0) { slot = EquipSlot.Wrists;    isAcc = true; return true; }
            if (cat.FingerR  != 0) { slot = EquipSlot.RingRight; isAcc = true; return true; }
            if (cat.FingerL  != 0) { slot = EquipSlot.RingLeft;  isAcc = true; return true; }
        }
        catch
        {
            // Sub-row might not resolve for some items — skip silently
        }

        return false;
    }
}
