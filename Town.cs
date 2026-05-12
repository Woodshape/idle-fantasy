#nullable enable

using Godot;
using GDict = Godot.Collections.Dictionary;

public partial class Town : Node2D
{
	[Export]
	public string DisplayName { get; set; } = "Town";

	[Export]
	public float ServiceRadius { get; set; } = 44.0f;

	public Vector2 ReturnPosition => GlobalPosition;

	public void Recover(Adventurer adventurer)
	{
		adventurer.RecoverToFull();
		GD.Print($"TOWN_RECOVER adventurer={adventurer.AdventurerName} town={DisplayName}");
		TestBridge.Instance?.EmitEvent("adventurer_recovered", new GDict
		{
			{ "source", nameof(Town) },
			{ "adventurer", adventurer.AdventurerName },
			{ "town", DisplayName },
			{ "health", adventurer.Health },
			{ "max_health", adventurer.MaxHealth }
		});
	}

	public override void _Draw()
	{
		DrawCircle(Vector2.Zero, ServiceRadius, new Color(0.18f, 0.44f, 0.30f, 0.35f));
		DrawCircle(Vector2.Zero, 18.0f, new Color(0.18f, 0.63f, 0.36f));
		DrawLine(new Vector2(-16.0f, 0.0f), new Vector2(16.0f, 0.0f), Colors.White, 3.0f);
		DrawLine(new Vector2(0.0f, -16.0f), new Vector2(0.0f, 16.0f), Colors.White, 3.0f);
	}
}
