using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using XIVPortStudio.Models;

namespace XIVPortStudio;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Last-selected equipment slot (index into <see cref="SlotInfo.AllSlots"/>).</summary>
    public int LastSlotIndex { get; set; } = 1;

    /// <summary>Last-used item search text.</summary>
    public string LastItemSearch { get; set; } = string.Empty;

    /// <summary>Row ID of the last-selected item in the game's Item sheet.</summary>
    public uint LastItemRowId { get; set; }

    /// <summary>Material set-ups keyed by item row ID.</summary>
    public Dictionary<uint, List<MaterialSetup>> MaterialsByItem { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
