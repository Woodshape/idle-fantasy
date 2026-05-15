#nullable enable

using Godot;

[GlobalClass]
public partial class EnemyGroupDefinition : Resource
{
	[Export]
	public string GroupId { get; set; } = string.Empty;

	[Export]
	public string DisplayName { get; set; } = string.Empty;

	[Export]
	public float SocialRadius { get; set; } = 96.0f;
}
