#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public sealed class ListPriorityDecisionPolicy : ICombatDecisionPolicy
{
	public CombatDecision ChooseAction(
		ICombatant actor,
		CombatLoadout loadout,
		IReadOnlyList<ICombatant> targetCandidates)
	{
		foreach (CombatAction action in loadout.Actions)
		{
			ICombatant? target = SelectBestTarget(actor, action, targetCandidates);

			if (!action.RequiresTarget || target is not null)
			{
				return new CombatDecision(action, target, "first_ready_action");
			}
		}

		return new CombatDecision(null, null, "no_ready_action");
	}

	private ICombatant? SelectBestTarget(ICombatant actor, CombatAction action, IReadOnlyList<ICombatant> targetCandidates)
	{
		if (!action.RequiresTarget)
		{
			return null;
		}

		return targetCandidates
			.Where(candidate => candidate.IsAlive)
			.OrderBy(candidate => GetDistanceSquared(actor, candidate))
			.ThenBy(candidate => candidate.CombatantId, StringComparer.Ordinal)
			.FirstOrDefault();
	}

	private float GetDistanceSquared(ICombatant actor, ICombatant candidate)
	{
		if (actor is Node2D actorNode && candidate is Node2D candidateNode)
		{
			return actorNode.GlobalPosition.DistanceSquaredTo(candidateNode.GlobalPosition);
		}

		return 0.0f;
	}
}
