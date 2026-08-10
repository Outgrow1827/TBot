# Changelog

## v3.5.0-beta2

> **Beta warning:** This release contains a substantial rework of the mine, research, Lifeforms and resource-planning workers. It has been tested with automated tests and live OGame sessions, but regressions or edge-case bugs may still remain.
>
> If you need the known functional stable version, use **v3.4.9**. It remains functional and is the recommended fallback while this beta is being validated.

### Brain planning rework

- Added a centralized account planner for AutoMine, AutoResearch, LifeformsAutoMine and LifeformsAutoResearch.
- Prioritized urgent infrastructure and energy work before ordinary mines, then ranked equivalent candidates by their calculated return or cost.
- Chained each celestial worker's first execution in the planned order, preventing concurrent workers from racing on shared resources while keeping randomized gaps.
- Reused the freshly returned celestial snapshot after every update, including lifeform buildings, lifeform research, facilities, buildings, resources, production and bonuses.
- Reset per-planet lifeform limits and applied the real lifeform cost reductions when comparing construction candidates.
- Added defensive planning guards for incomplete Fast snapshots so a missing optional payload cannot crash AutoMine.
- Fixed the AutoResearch plasma prerequisite check using a logical operator.
- Reworked multiple-origin resource planning to reserve resources and cargo capacity before sending fleets, merge demands for the same destination and reject incomplete batches instead of sending unusable partial transports.
- Fixed multi-origin cargo calculation to use the bonuses of the actual sending origin and stopped the planner from mutating destination snapshots.
- Added regression tests for deterministic construction ordering, resource reservation, multi-origin splitting, incomplete transport batches and sub-threshold allocation rollback.

#### Before vs What to expect now

| Situation | Before | What to expect now |
| --- | --- | --- |
| P1 has a Terraformer candidate while P2 has a profitable Crystal Mine | Each celestial worker could start independently, so the effective order depended on timing and semaphore acquisition. | P1 is selected first because the Terraformer has account-level priority; P2 follows after P1's first action. |
| A priority building competes with a profitable mine | The mine return could determine the order even when infrastructure or energy was the real bottleneck. | Terraformer, energy, storage/crawler and infrastructure needs are considered before ordinary mine ROI. |
| Mine, research and Lifeforms workers run in the same cycle | Workers could read stale data from the previous update or race while using shared resource state. | Each update is based on the latest returned snapshot, and celestial workers are chained so one first action completes before the next begins. |
| Lifeform limits are evaluated across planets | Limits calculated for one planet could leak into the next planet's decision. | Every planet gets a fresh set of lifeform limits and its own bonuses/cost reductions. |
| A Fast planet response lacks optional planning data | AutoMine could throw a null-reference exception while deciding whether to build crawlers or the next building. | The planet is skipped for that planning branch until the required data exists; the worker continues safely. |
| One destination needs 300k resources and two origins can provide 150k each | The same resources could be considered more than once, or a partial fleet could be sent even though the construction still could not start. | The planner reserves 150k from each origin and creates two complete planned shipments. |
| A target needs 100k metal plus 100k crystal, but an origin has only 100k metal | A partial allocation could be sent even though the construction still could not start. | The incomplete allocation is rejected; no known-incomplete batch is sent. |
| A source contributes less than the configured minimum shipment size | A sub-threshold partial allocation could consume simulated capacity and make the rest of the plan incorrect. | The partial allocation is rolled back and the planner tries another valid source or sends nothing. |
| Origin A has a +20% lifeform cargo bonus while Origin B has +0% | Cargo requirements could be calculated with the destination or current celestial's bonus instead of the real sender's bonus. | Each fleet uses the cargo bonus of its actual origin, so A and B are evaluated independently. |

### Artifact inventory

- Added the ogamed v13 artifact inventory endpoint and exposed collected artifacts and capacity to TBot.
- Validated the endpoint against a live Xanthippe account (`272 / 3600` returned successfully).

### AutoFarm report integrity and activity view

- Prevented AutoFarm from processing an espionage report after its report ID has already been used for an attack.
- Kept targets in `AttackSent` while an attack fleet is still in progress, including attacks sent from another configured origin.
- Persisted consumed report IDs and an idempotent attack history in the existing AutoFarm SQLite database.
- Added a read-only English AutoFarm page showing current reports, pending attacks, recent dispatches and the cached system count.

#### Before vs What to expect now

| Situation | Before | What to expect now |
| --- | --- | --- |
| OGame keeps an old espionage report after a delete request fails | The report could be read again in a later cycle and become attackable again. | The report ID is persisted as consumed and is skipped permanently for AutoFarm processing. |
| An attack is still travelling or returning when the next AutoFarm cycle starts | The target could be reset too early and compete with the old report again. | The target stays in `AttackSent` until no matching attack fleet remains. |
| TBot is restarted after an attack | The in-memory target state was not enough to prove that a report had already been used. | The consumed report ledger and attack history are reloaded from SQLite. |
| You need to understand what AutoFarm has done | The information was spread across logs and raw SQLite rows. | The WebUI has a simple read-only AutoFarm page with reports, statuses, coordinates and attack history. |

## v3.4.9

### AutoDiscovery

- Fixed the v13 availability check to use the supported planet-scoped ogamed endpoint.
- Removed the obsolete account-level endpoint so future callers cannot reintroduce the same failure.
- Confirmed with a live Xanthippe account test that AutoDiscovery queries the system view and sends a discovery fleet successfully.

## v3.4.8

### AutoFarm

- Replaced the six-hour JSON empty-system cache with a persistent SQLite state store.
- Added configurable `DaysToKeepOldSystemData` (default: 7) and `EmptySystemCooldownDays` (default: 30).
- Only systems with no planets or only vacation-mode planets receive the long empty-system cooldown.
- Systems with incomplete or failed responses are never cached as empty.
- Persisted viewed systems and farm targets across restarts, while reusing fresh cached system data.
- Counted espionage and attack fleets together against AutoFarm's `MaxSlots` budget.
- Stopped failed fleet submissions from invalidating targets and allowed the worker to continue using remaining slots.
- Continued scanning and scheduling eligible targets instead of sleeping until the longest attack returns.
- Preserved manual and mixed fleets from AutoFarm ownership and fixed circular range progression.

### AutoDiscovery

- Used the system-view discovery availability instead of probing every planet position blindly.
- Persisted a cursor per origin and advanced it after blacklisted, rejected, or failed positions, including the last position of a system.
- Kept account-wide discovery-slot accounting and stopped when no eligible discovery slot remains.

### Release

- Published as a patch release because v3.4.7 is already public.
- Release packages contain clean default settings and the OGame v13-compatible `ogamed` binary.

## v3.4.7

### OGame v13

- Updated the bundled `ogamed` submodule to `ronnie32/ogame.mod` v13-fix.4.
- Fixed expedition fleet composition and origin handling for OGame v13.
- Fixed AutoDiscovery cursor advancement after blacklisted or rejected positions.
- Fixed AutoDiscovery fleet and discovery-slot accounting.

### AutoFarm

- Added persistent cooldowns for systems confirmed to have no eligible targets.
- Avoided refreshing every origin's ship data on every scan cycle.
- Deduplicated configured farm origins and reused the cached fleet state.
- Kept incomplete galaxy responses out of the empty-system cache.
- Continued scanning when an individual system request fails.
- Preserved targets even when another attack to the same target is already in progress.

### Validation

- TBot solution builds successfully with .NET 9.
- TBot unit tests pass.
- ogame bridge tests pass.
- AutoFarm was exercised against a real OGame v13 account: the first empty-system scan created a persistent cache, and the next cycle skipped that system without rescanning or sending a fleet.
