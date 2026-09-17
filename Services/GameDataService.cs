using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
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

    /// <summary>
    /// Returns the race/gender combinations that already have their own model file
    /// for this item by default (as opposed to falling back to another race's model).
    /// </summary>
    public HashSet<RaceGender> GetNativeModelRaces(EquipSlot slot, ushort modelId)
    {
        var result = new HashSet<RaceGender>();
        foreach (var rg in RaceInfo.AllRaces)
        {
            var path = ModelNaming.ModelGamePath(slot, modelId, rg.RaceCode);
            try
            {
                if (_data.FileExists(path))
                    result.Add(rg);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[XPS] FileExists check failed for {0}", path);
            }
        }
        return result;
    }

    /// <summary>Reads the raw, unmodified bytes of a game file, or null if it doesn't exist.</summary>
    public byte[]? GetVanillaFileBytes(string gamePath)
    {
        try
        {
            return _data.GetFile(gamePath)?.Data;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[XPS] Failed to read vanilla file {0}", gamePath);
            return null;
        }
    }

    /// <summary>
    /// Reads and parses the skeleton/bone metadata of the vanilla, unmodified model
    /// at a game path, or null if it doesn't exist or fails to parse. Reads raw bytes
    /// and parses them with <see cref="VanillaModelReader"/> rather than Lumina's own
    /// typed model reader, which throws on every retail model tested against the
    /// currently installed Dalamud/Lumina build — see VanillaModelReader's doc comment.
    /// </summary>
    public VanillaModelInfo? GetVanillaModel(string gamePath, out string? error)
    {
        error = null;
        try
        {
            var bytes = _data.GetFile(gamePath)?.Data;
            if (bytes == null)
            {
                error = "file not found";
                return null;
            }
            return VanillaModelReader.Read(bytes);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[XPS] Failed to read vanilla model {0}", gamePath);
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Finds a vanilla material to use as a clone target for a new .mtrl: tries every
    /// letter suffix ("_a".."_z") at the requested material variant, then falls back to
    /// variant 1 (which virtually always has at least a "_a" material). Returns null if
    /// nothing usable is found.
    /// </summary>
    public (string GamePath, MtrlFile Material, byte[] RawBytes)? FindVanillaMaterialTemplate(EquipSlot slot, ushort modelId, int materialVariant)
    {
        foreach (var variant in new[] { materialVariant, 1 }.Distinct())
        {
            for (int letter = 1; letter <= 26; letter++)
            {
                var name = MaterialNaming.DefaultName(slot, modelId, letter);
                var path = MaterialNaming.MaterialGamePath(slot, modelId, variant, name);
                try
                {
                    if (!_data.FileExists(path)) continue;

                    var raw = _data.GetFile(path)?.Data;
                    var parsed = _data.GetFile<MtrlFile>(path);
                    if (raw != null && parsed != null)
                        return (path, parsed, raw);
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "[XPS] Failed to read candidate material template {0}", path);
                }
            }
        }
        return null;
    }

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
