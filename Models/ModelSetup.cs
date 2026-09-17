using System;

namespace XIVPortStudio.Models;

/// <summary>
/// A model configured for one race/gender of the selected item. <see cref="SourcePath"/>
/// points to a local .mdl file that gets copied into the mod at that race's game path.
/// Races without a native model entry by default need an Eqdp meta manipulation so
/// Penumbra tells the game to use this race's own model instead of falling back to
/// another race's file — see <see cref="ModEqdpOverride"/>.
/// </summary>
[Serializable]
public class RaceModelEntry
{
    public PlayerRace   Race       { get; set; } = PlayerRace.Midlander;
    public PlayerGender Gender     { get; set; } = PlayerGender.Male;

    /// <summary>Local .mdl file copied into the mod for this race. Empty until set up.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="SourcePath"/> is a plain copy of the vanilla model for this
    /// race, dropped in as a starting point for the user to edit externally (e.g. in
    /// Blender via InstantEdit) rather than an already-finished custom model.
    /// </summary>
    public bool IsVanillaDummy { get; set; }

    public RaceGender RaceGender => new(Race, Gender);

    public RaceModelEntry Clone() => new()
    {
        Race           = Race,
        Gender         = Gender,
        SourcePath     = SourcePath,
        IsVanillaDummy = IsVanillaDummy,
    };
}
