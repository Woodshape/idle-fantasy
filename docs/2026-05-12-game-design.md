# Idle Fantasy Game Design Notes

## High Concept

Idle Fantasy is a 2D top-down fantasy RPG simulator with quasi-idle progression. The player manages settlements that attract adventurers, while adventurers autonomously leave town, explore the surrounding world, fight monsters, collect loot, return to resupply, level up, and repeat.

The player is primarily an observer and planner, not a direct character controller. The core fantasy is watching a living RPG world run on its own, then nudging the economy, roster, infrastructure, and risk profile so the adventurer ecosystem grows from a small village into a city-scale fantasy hub.

## Core Loop

1. Adventurers are hired manually from a settlement, spending player gold.
2. Adventurers pick a goal based on level, health, gear, nearby threats, and available locations.
3. They travel into the world.
4. They fight monsters, explore ruins, or enter dungeons.
5. They collect loot, gold, experience, and injuries.
6. They decide whether to continue, retreat, or die based on risk and supplies.
7. Survivors return to settlement.
8. They buy potions, upgrade equipment, rest, level up, or retire.
9. Settlement services improve, attracting stronger adventurers.

## Player Role

The player influences the simulation through long-term management decisions:

- Hire adventurers manually with gold.
- Upgrade village, town, and city services.
- Fund shops, inns, clerics, training halls, guilds, and defenses.
- Set broad policies such as risk tolerance, hunting zones, bounty priorities, and expedition funding.
- Cast spells that temporarily affect the world, adventurers, monsters, or economy.
- Watch individual adventurer stories emerge from autonomous decisions.

The player is abstract, not a visible character in the world. Direct control should normally be limited. The game should not become a manual action RPG by default, but spells can create short, intentional windows where the player intervenes directly.

There is no pause planned for now. The simulation should keep running, which makes timing, risk, and intervention costs matter.

Later progression may let the abstract player build additional buildings, research upgrades, unlock automation, or auto-hire adventurers. Early gameplay should stay relatively idle and management-light.

If the player runs out of money, a fallback recruitment option may be needed. One possibility is hiring a downtrodden adventurer: weak, desperate, cheap or free, and risky to rely on.

## Time Model

Idle Fantasy uses a hybrid time model.

- Game mechanics are tick-based.
- Attacks, casting, buffs, debuffs, cooldowns, and similar systems resolve on ticks.
- Movement and world time advance continuously so actors feel fluid and dynamic.
- The player should see smooth travel and animation even when combat math uses discrete resolution points.

This gives deterministic structure for mechanics while preserving the feel of a living top-down world.

## Player Spells

Player spells are active interventions layered on top of the idle simulation. They let the player influence outcomes without replacing the autonomous game loop.

Potential spell categories:

- Guidance: reveal threats, highlight valuable ruins, improve target selection, or redirect an adventurer.
- Protection: shield an adventurer, reduce incoming damage, prevent ambushes, or improve escape chance.
- World influence: slow monsters, bless a road, reveal hidden loot, or weaken a dungeon room.
- Economy influence: boost shop stock, reduce upgrade cost, improve loot sale prices, or attract recruits.
- Possession: temporarily take direct control of an adventurer.

Spells should have meaningful costs or cooldowns so the player chooses where intervention matters. Possible costs include mana, favor, settlement resources, rare relics, cooldown time, or opportunity cost.

### Possession Spell

Possession lets the player take control of one adventurer for a fixed duration.

During possession:

- The selected adventurer stops autonomous decision-making.
- The player can click to move.
- The player can click to target enemies or objects.
- The player can use that adventurer's available skills.
- The adventurer still uses their own stats, gear, health, inventory, and risk.
- The simulation continues around them.

When possession ends:

- Control returns to the adventurer AI.
- The adventurer evaluates their current situation and resumes an appropriate state.
- If they are hurt, overloaded, lost, or in danger, they may retreat rather than continue the player's plan.

Design intent:

- Possession gives the player dramatic agency at key moments.
- It should be time-limited so idle simulation remains the core identity.
- It creates a reason to care about individual adventurer builds and skills.
- It can be gated by progression, spell cooldowns, or settlement magic infrastructure.

## Settlements

Settlements are progression anchors and safe zones.

- Villages attract low-level adventurers and provide basic services.
- Towns attract stronger adventurers and offer better gear, potions, training, and rumors.
- Cities attract top-level characters and unlock advanced systems such as resurrection, magic guilds, high-tier crafting, and large-scale quests.

Potential settlement services:

- Tavern: manually recruits adventurers for gold.
- Inn: restores health over time.
- Shop: sells potions and basic supplies.
- Blacksmith: sells and repairs equipment.
- Trainer: converts experience into levels or skill improvements.
- Temple: healing, curse removal, resurrection.
- Guild hall: party formation, contracts, bounties, policy controls.
- Market: loot sale prices, resource flow, settlement economy.

## Adventurers

Adventurers are autonomous agents with persistent state.

Core stats:

- Name and class.
- Level and experience.
- Health and max health.
- Attack, defense, speed, crit chance, crit damage, and perception.
- Gold, inventory, equipment, and potion count.
- Current task and target.
- Risk tolerance and personality traits.

Initial classes:

- Fighter: durable melee adventurer, good starter class.
- Rogue: faster travel, better loot chance, weaker in direct combat.
- Cleric: healing and eventual resurrection support.
- Mage: high damage, fragile, expensive scaling.

Early implementation should start with one generic adventurer archetype before class complexity is added.

## Parties

Adventurers can create, join, and leave parties. A party is a temporary or persistent group of adventurers that travels, fights, and shares rewards.

Party tradeoff:

- Solo adventuring is high risk and high reward.
- Party adventuring is lower risk but rewards are split between members.
- Parties can defeat stronger monsters, survive deeper expeditions, and recover from bad encounters more reliably.
- Solo adventurers progress faster if they survive.

Party behavior:

- Adventurers may seek a party when a target is too dangerous alone.
- Adventurers may leave a party if rewards are too low, personalities clash, health is poor, or goals diverge.
- Parties should choose targets based on combined strength, supplies, travel distance, and expected reward.
- Party members split gold, loot, and experience according to simple rules at first.

Initial party reward model:

- Gold is split evenly.
- Experience is split evenly or weighted by participation.
- Items are assigned randomly, by class usefulness, or sold and split later.

Later party systems:

- Roles such as tank, healer, scout, damage dealer, and support.
- Party reputation and named parties.
- Party tactics, formations, morale, and leadership.
- Contracts requiring minimum party strength.
- Rescue parties for dead or stranded adventurers.

## Adventurer Decision Model

Adventurers should feel self-directed. Early behavior can use a simple finite state machine:

- IdleInTown: choose whether to rest, shop, level up, or depart.
- ChooseTarget: select a nearby monster or location appropriate to level.
- FindParty: seek allies when a target is too risky alone.
- JoinParty: coordinate with other adventurers and follow party decisions.
- TravelToTarget: move through the world.
- Fight: resolve combat against a monster.
- Loot: collect rewards.
- ReturnToTown: retreat with loot or when health/supplies are low.
- RecoverInTown: heal and resupply.
- Possessed: temporarily controlled by the player until the spell expires.
- Dead: remain dead until resurrection or permanent removal.

Later behavior can become utility-based, where each possible action is scored by expected reward, risk, travel distance, supplies, and personality.

## Monsters

Monsters are world threats and resource generators.

Core monster data:

- Type.
- Level.
- Health.
- Attack and defense.
- Aggro radius.
- Loot table.
- Experience reward.
- Spawn location or lair.

Early monsters should be simple static targets near the starting town. Later systems can add wandering monsters, lairs, patrols, boss monsters, ecology pressure, and attacks on settlements.

## World Locations

World locations create goals and progression pressure.

- Wilderness: low-risk monster hunting.
- Ruins: higher loot, higher danger, limited visibility.
- Dungeons: multi-room expeditions with escalating risk.
- Roads: safer travel, possible ambushes.
- Resource nodes: herbs, ore, relics, monster dens.

Locations should communicate risk clearly through distance, monster level, biome, rumors, or scouting.

The world should start procedural because it will be simpler to test, iterate, and rebalance while the game loop is still changing. Handcrafted content can be added later once the core systems are stable.

For the first slice, no real world map is required. The minimum useful setup is a town, one or more adventurers, and monsters.

## Combat

Combat should be random, but the randomness should come from explicit probability calculations.

Attacks, defenses, spells, resists, dodges, blocks, and similar combat actions should first produce a fixed probability under the hood, either as `0..1` or `0..100`. The game then rolls against that probability to determine whether the action succeeds.

Example:

```text
hit_chance = f(attacker_accuracy, defender_evasion, level_delta, status_effects)
hit_roll = random 0..1
attack_hits = hit_roll <= hit_chance

crit_chance = clamp(0.10 + attacker_crit_chance, 0.0, 1.0)
crit_roll = random 0..1 after a hit
attack_crits = attack_hits && crit_roll <= crit_chance
```

This keeps formulas inspectable and tunable while still making combat unpredictable.

Minimal combat loop:

1. Adventurer and monster enter combat.
2. Combatants exchange attacks at fixed intervals.
3. Each attack computes a success probability and rolls against it.
4. Successful attacks roll separately for critical hits.
5. Critical hits multiply attack, ability, or spell damage before defense by the attacker's crit damage.
6. Successful attacks apply damage, mitigation, effects, or spell outcomes.
7. Health reaches zero.
8. Winner receives rewards or enters a post-combat state.

Later combat can add abilities, status effects, formations, party tactics, fleeing, potions, and class-specific logic.

## Loot And Economy

Loot is the resource bridge between adventure and settlement progression.

Basic loot types:

- Gold.
- Equipment.
- Potions and consumables.
- Materials.
- Rare relics.

Early loop:

- Monsters drop gold and experience.
- Adventurers carry gold back to town.
- Adventurers spend gold on healing or potions.
- Excess wealth can indirectly benefit the settlement later.

The economy should eventually support visible flows: adventurers buy from shops, shops need stock, settlement upgrades improve available goods, and high-level adventurers require high-level services.

## Death And Resurrection

Death should matter because idle observation gains drama from risk.

Initial behavior:

- A dead adventurer stops acting.
- Death is logged visibly.
- Their carried loot may be lost or dropped.

Long-term behavior:

- Resurrection is expensive.
- Resurrection requires a high-level cleric, temple, rare item, or city-tier service.
- Some deaths may be permanent depending on game settings or difficulty.

## Progression

Progression should be layered:

- Individual progression: adventurers gain levels, gear, traits, and reputation.
- Settlement progression: services improve and attract better adventurers.
- World progression: stronger monsters, deeper dungeons, new regions, and larger threats appear.
- Player progression: better policies, automation, analytics, and strategic tools unlock over time.

## First Playable Slice Target

The first slice should prove the smallest complete version of the loop:

- A town exists as a home point.
- One adventurer starts in the town.
- One monster spawn exists outside the town.
- The adventurer autonomously walks to a monster.
- Combat resolves with probability-based attack rolls.
- The adventurer gains gold and experience if successful.
- The adventurer returns to the town.
- The state is visible through logs, labels, or TestBridge events.

This does not need a world map, shops, classes, dungeons, parties, equipment, or full settlement progression yet.

## Current Design Decisions

- Adventurers are hired manually with gold at first.
- Auto-hiring may be added later.
- If the player runs out of money, a desperate or downtrodden adventurer fallback may be considered.
- Time is hybrid: tick-based mechanics with continuous movement and world time.
- There is no pause planned for now.
- Combat is random and should use explicit probability formulas followed by rolls.
- Far-away adventurer simulation is undecided and irrelevant for the first slice.
- The player is abstract and mostly idle for now.
- Later player progression may include building additional structures or researching mechanics.
- The world should start procedural for easier testing.
- The first slice does not need a world map, only a town, adventurers, and monsters.

## Open Design Questions

- Are adventurers permanently simulated off-screen, or only near loaded areas?
- What exact formula family should attack, defense, casting, dodge, block, and resist rolls use?
- How generous should the no-money fallback adventurer option be?
- What is the player's abstract identity: patron, guild spirit, godlike observer, town charter, or something else?
