#nullable enable

using Godot;
using System;
using GDict = Godot.Collections.Dictionary;

public sealed class DataBackedLoadoutSource : ICombatLoadoutSource
{
	private readonly GameContentLibrary _content;
	private readonly Action<string, GDict>? _emitEvent;

	public DataBackedLoadoutSource(GameContentLibrary content, Action<string, GDict>? emitEvent = null)
	{
		_content = content;
		_emitEvent = emitEvent;
	}

	public CombatLoadout ResolveLoadout(ICombatant combatant)
	{
		if (combatant is Adventurer adventurer)
		{
				if (TryResolveAdventurer(adventurer, out CombatLoadout? loadout))
				{
					return loadout!;
				}
		}
		else if (combatant is Monster monster)
		{
				if (TryResolveMonster(monster, out CombatLoadout? loadout))
				{
					return loadout!;
				}
		}

		EmitContentFallback(combatant, "missing_definition_or_loadout");
		return new CombatLoadout(
			"fallback_basic_attack",
			string.Empty,
			new[] { CombatAction.BasicAttack() });
	}

	private bool TryResolveAdventurer(Adventurer adventurer, out CombatLoadout? loadout)
	{
		loadout = null;
		string definitionId = !string.IsNullOrWhiteSpace(adventurer.DefinitionId)
			? adventurer.DefinitionId
			: adventurer.Archetype == AdventurerArchetype.Mage ? "mage" : "warrior";

		if (!_content.TryGetAdventurer(definitionId, out AdventurerDefinition? definition)
			|| definition?.CombatLoadout is null)
		{
			return false;
		}

		loadout = _content.CreateActionsForLoadout(definition.CombatLoadout, definition.DefinitionId);
		return true;
	}

	private bool TryResolveMonster(Monster monster, out CombatLoadout? loadout)
	{
		loadout = null;
		string definitionId = !string.IsNullOrWhiteSpace(monster.DefinitionId)
			? monster.DefinitionId
			: "slime";

		if (!_content.TryGetMonster(definitionId, out MonsterDefinition? definition)
			|| definition?.CombatLoadout is null)
		{
			return false;
		}

		loadout = _content.CreateActionsForLoadout(definition.CombatLoadout, definition.DefinitionId);
		return true;
	}

	private void EmitContentFallback(ICombatant combatant, string reason)
	{
		GD.PushError($"Content loadout fallback used for {combatant.CombatantKind} '{combatant.DisplayName}': {reason}.");
		_emitEvent?.Invoke("content_validation_failed", new GDict
		{
			{ "source", nameof(DataBackedLoadoutSource) },
			{ "combatant", combatant.DisplayName },
			{ "combatant_kind", combatant.CombatantKind },
			{ "reason", reason }
		});
	}
}
