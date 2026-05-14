#nullable enable

using Godot;

[GlobalClass]
public partial class AdventurerSpawnDefinition : Resource
{
	[Export]
	public string NodeName { get; set; } = string.Empty;

	[Export]
	public string DisplayNameOverride { get; set; } = string.Empty;

	[Export]
	public AdventurerDefinition? ActorDefinition { get; set; }

	[Export]
	public Vector2 Position { get; set; }

	[Export]
	public bool PositionIsTownRelative { get; set; } = true;

	[Export]
	public bool Enabled { get; set; } = true;
}
