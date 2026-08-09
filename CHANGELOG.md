# Changelog

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
