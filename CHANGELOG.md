# Changelog

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
