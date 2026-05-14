#nullable enable

using Godot;
using System;
using System.Linq;

[GlobalClass]
public partial class CombatLoadoutDefinition : Resource
{
	[Export]
	public string LoadoutId { get; set; } = string.Empty;

	[Export]
	public string DisplayName { get; set; } = string.Empty;

	[Export]
	public CombatActionDefinition[] Actions { get; set; } = Array.Empty<CombatActionDefinition>();

	public CombatLoadout ToRuntimeLoadout(string definitionId)
	{
		CombatAction[] actions = Actions
			.Where(action => action is not null)
			.Select(action => action.ToRuntimeAction())
			.ToArray();

		return new CombatLoadout(LoadoutId, definitionId, actions);
	}
}
