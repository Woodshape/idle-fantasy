#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public sealed class CombatTickResolver
{
	private readonly RandomNumberGenerator _rng;
	private readonly ICombatDecisionPolicy _decisionPolicy;
	private readonly Action<string, GDict> _emitEvent;
	private readonly string _eventSource;

	public CombatTickResolver(
		RandomNumberGenerator rng,
		ICombatDecisionPolicy decisionPolicy,
		Action<string, GDict> emitEvent,
		string eventSource)
	{
		_rng = rng;
		_decisionPolicy = decisionPolicy;
		_emitEvent = emitEvent;
		_eventSource = eventSource;
	}

	public CombatTickResult ResolveTick(
		int encounterId,
		long tick,
		double tickIntervalSeconds,
		IReadOnlyList<Adventurer> adventurers,
		IReadOnlyList<Monster> monsters,
		IReadOnlyList<CombatActionRunner> runners,
		Func<CombatActionRunner, IReadOnlyList<ICombatant>> getTargetCandidates,
		Func<CombatActionRunner, ICombatant?> getPreferredTarget,
		Func<bool> handleEndConditions)
	{
		_emitEvent("combat_tick_started", new GDict
		{
			{ "source", _eventSource },
			{ "encounter_id", encounterId },
			{ "tick", tick },
			{ "tick_interval_seconds", tickIntervalSeconds },
			{ "adventurer_count", adventurers.Count },
			{ "monster_count", monsters.Count }
		});

		foreach (CombatActionRunner runner in runners)
		{
			runner.AdvanceTickCounters(tick);
		}

		List<QueuedCombatAction> queuedActions = new();

		foreach (CombatActionRunner runner in runners)
		{
			AddIfQueued(queuedActions, runner.GetQueuedActionForTick(tick));

			if (runner.CanChooseAction)
			{
				CombatDecision decision = _decisionPolicy.ChooseDecision(new CombatDecisionContext(
					runner,
					getTargetCandidates(runner),
					getPreferredTarget(runner)));
				AddIfQueued(queuedActions, runner.QueueDecisionForTick(decision, tick));
			}
		}

		foreach (RolledCombatAction rolledAction in RollActionOrder(encounterId, queuedActions, tick))
		{
			rolledAction.QueuedAction.Runner.ResolveQueuedAction(rolledAction.QueuedAction, tick);
		}

		bool encounterEnded = handleEndConditions();
		int livingAdventurerCount = adventurers.Count(adventurer => adventurer.IsAlive);
		int livingMonsterCount = monsters.Count(monster => monster.IsAlive);

		_emitEvent("combat_tick_completed", new GDict
		{
			{ "source", _eventSource },
			{ "encounter_id", encounterId },
			{ "tick", tick },
			{ "queued_action_count", queuedActions.Count },
			{ "adventurer_alive", adventurers.FirstOrDefault()?.IsAlive ?? false },
			{ "monster_alive", monsters.FirstOrDefault()?.IsAlive ?? false },
			{ "living_adventurer_count", livingAdventurerCount },
			{ "living_monster_count", livingMonsterCount }
		});

		return new CombatTickResult(
			tick,
			queuedActions.Count,
			encounterEnded,
			livingAdventurerCount,
			livingMonsterCount);
	}

	private IEnumerable<RolledCombatAction> RollActionOrder(
		int encounterId,
		IReadOnlyList<QueuedCombatAction> queuedActions,
		long tick)
	{
		List<RolledCombatAction> rolledActions = new();

		foreach (QueuedCombatAction queuedAction in queuedActions)
		{
			int randomRoll = _rng.RandiRange(1, 100);
			CombatStats stats = queuedAction.Actor.Stats;
			int attackSpeedBonus = GetAttackSpeedInitiativeBonus(stats.AttackSpeedTicks);
			int initiativeScore =
				randomRoll
				+ stats.Initiative
				+ attackSpeedBonus
				- queuedAction.Action.ActionWeight;
			RolledCombatAction rolledAction = new(queuedAction, randomRoll, initiativeScore);
			rolledActions.Add(rolledAction);
			_emitEvent("combat_action_order_rolled", new GDict
			{
				{ "source", _eventSource },
				{ "encounter_id", encounterId },
				{ "tick", tick },
				{ "combatant", queuedAction.Actor.DisplayName },
				{ "combatant_kind", queuedAction.Actor.CombatantKind },
				{ "target", queuedAction.Target?.DisplayName ?? "none" },
				{ "action_id", queuedAction.Action.ActionId },
				{ "random_roll", randomRoll },
				{ "initiative", stats.Initiative },
				{ "attack_speed_ticks", stats.AttackSpeedTicks },
				{ "attack_speed_bonus", attackSpeedBonus },
				{ "action_weight", queuedAction.Action.ActionWeight },
				{ "initiative_score", initiativeScore }
			});
		}

		return rolledActions
			.OrderByDescending(action => action.InitiativeScore)
			.ThenByDescending(action => action.QueuedAction.Actor.Initiative)
			.ThenBy(action => action.QueuedAction.Actor.AttackSpeed)
			.ThenBy(action => action.QueuedAction.Actor.CombatantId, StringComparer.Ordinal)
			.ToArray();
	}

	private static int GetAttackSpeedInitiativeBonus(int attackSpeedTicks)
	{
		return Math.Max(0, 12 - Math.Max(1, attackSpeedTicks));
	}

	private static void AddIfQueued(List<QueuedCombatAction> queuedActions, QueuedCombatAction? queuedAction)
	{
		if (queuedAction is not null)
		{
			queuedActions.Add(queuedAction);
		}
	}
}
