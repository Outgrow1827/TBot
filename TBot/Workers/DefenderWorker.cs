using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TBot.Common.Logging;
using Tbot.Helpers;
using TBot.Model;
using Tbot.Services;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Tbot.Includes;
using System.Timers;
using TBot.Ogame.Infrastructure;
using Tbot.Common.Settings;

namespace Tbot.Workers {
	internal class DefenderWorker : WorkerBase {
		private readonly IFleetScheduler _fleetScheduler;
		private readonly IOgameService _ogameService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		// #17 (ideia vista no OgameBot): quando alguém só te espiona (IgnoreProbes=true), o bot ignora
		// silenciosamente sem nem avisar - esse dicionário guarda, por origem, até quando não repetir o
		// aviso de Telegram, pra não floodar quando o mesmo jogador manda várias sondas seguidas. Só
		// precisa viver em memória (não persistido): um reinício do bot no pior caso manda um aviso a
		// mais, não é um problema de segurança/farm como o resto do estado persistido em SQLite.
		private readonly Dictionary<string, DateTime> _spyWatchNotifiedUntil = new();
		public DefenderWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ITBotOgamedBridge tbotOgameBridge)
			: base(parentInstance) {
			_fleetScheduler = fleetScheduler;
			_ogameService = ogameService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		protected override async Task Execute() {
			try {
				DoLog(LogLevel.Information, "Checking attacks...");

				await FakeActivity();
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				await _fleetScheduler.ReconcilePendingRecalls();
				bool isUnderAttack = await _ogameService.IsUnderAttack();
				DateTime time = await _tbotOgameBridge.GetDateTime();
				if (isUnderAttack) {
					if ((bool) _tbotInstance.InstanceSettings.Defender.Alarm.Active)
						await Task.Factory.StartNew(() => ConsoleHelpers.PlayAlarm(), _ct);
					// UpdateTitle(false, true);
					DoLog(LogLevel.Warning, "ENEMY ACTIVITY!!!");
					_tbotInstance.UserData.attacks = await _ogameService.GetAttacks();
					foreach (AttackerFleet attack in _tbotInstance.UserData.attacks) {
						HandleAttack(attack);
					}
				} else {
					DoLog(LogLevel.Information, "Your empire is safe");
				}
				long interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMax);
				if (interval <= 0)
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);

				// Display dateTime for logging 
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"An error has occurred while checking for attacks: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				DateTime time = await _tbotOgameBridge.GetDateTime();
				long interval = RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
			} finally {

			}
		}
		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool) _tbotInstance.InstanceSettings.Defender.Active;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "Defender";
		}
		public override Feature GetFeature() {
			return Feature.Defender;
		}

		public override LogSender GetLogSender() {
			return LogSender.Defender;
		}


		private async Task FakeActivity() {
			//checking if under attack by making activity on planet/moon configured in settings (otherwise make acti on latest activated planet)
			// And make activity on one more random planet to fake real player

			Celestial celestial;
			Celestial randomCelestial;
			var randomActivity = (bool) _tbotInstance.InstanceSettings.Defender.RandomActivity;

			if (randomActivity == false) {
				celestial = _tbotInstance.UserData.celestials
				.Unique()
				.Where(c => c.Coordinate.Galaxy == (int) _tbotInstance.InstanceSettings.Defender.Home.Galaxy)
				.Where(c => c.Coordinate.System == (int) _tbotInstance.InstanceSettings.Defender.Home.System)
				.Where(c => c.Coordinate.Position == (int) _tbotInstance.InstanceSettings.Defender.Home.Position)
				.Where(c => c.Coordinate.Type == Enum.Parse<Celestials>((string) _tbotInstance.InstanceSettings.Defender.Home.Type))
				.SingleOrDefault() ?? new() { ID = 0 };

				if (celestial.ID != 0) {
					DoLog(LogLevel.Information, $"Check from Home ({celestial.Coordinate.Galaxy}:{celestial.Coordinate.System}:{celestial.Coordinate.Position} {celestial.Coordinate.Type})");
					celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Defences);
				}
			} else {
				randomCelestial = _tbotInstance.UserData.celestials.Shuffle().FirstOrDefault() ?? new() { ID = 0 };

				if (randomCelestial.ID != 0) {
					DoLog(LogLevel.Information, $"Check from Random Celestial");
					randomCelestial = await _tbotOgameBridge.UpdatePlanet(randomCelestial, UpdateTypes.Defences);
				}
			}
			return;
		}

		/// <summary>
		/// #17: notifies (Telegram) when someone spies us, since IgnoreProbes normally makes that case
		/// return silently right after this call with no other trace. Cooldown is per attack origin so a
		/// player re-probing repeatedly doesn't flood the chat.
		/// </summary>
		private async Task NotifySpyWatch(AttackerFleet attack) {
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "SpyWatch") ||
				!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender.SpyWatch, "Active") ||
				!(bool) _tbotInstance.InstanceSettings.Defender.SpyWatch.Active)
				return;

			string originKey = attack.Origin?.ToString() ?? attack.AttackerID.ToString();
			DateTime now = DateTime.UtcNow;
			if (_spyWatchNotifiedUntil.TryGetValue(originKey, out DateTime notifiedUntil) && now < notifiedUntil)
				return;

			int cooldownMinutes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender.SpyWatch, "CooldownMinutes")
				? (int) _tbotInstance.InstanceSettings.Defender.SpyWatch.CooldownMinutes : 30;
			_spyWatchNotifiedUntil[originKey] = now.AddMinutes(cooldownMinutes);

			await _tbotInstance.SendTelegramMessage($"Player {attack.AttackerName} ({attack.AttackerID}) is spying your celestial {attack.Destination} from {attack.Origin}.");
		}

		// #17/#20 follow-up: cooldown per (attacker, coordinate) pair for the actual counter-spy send, not
		// just the Telegram notification (_spyWatchNotifiedUntil above) - without this, a player probing
		// the same coordinate 10x in a row would have us dumb-fire 10 counter-spy waves back at them.
		private readonly Dictionary<string, DateTime> _spyBackSentUntil = new();

		/// <summary>
		/// Spies back at whoever attacked/spied us: not just the origin celestial, but every coordinate
		/// we've ever recorded for that player (PlayersDatabase.KnownCoordinates - built up from every
		/// attack/spy/farm sighting, see PlayersDatabase), each together with its sibling celestial at the
		/// same galaxy:system:position (if they spied from a Moon, also spy the Planet there, and
		/// vice-versa). Doesn't discover the attacker's planets we've never directly observed - ogamed has
		/// no "list all of a player's planets" API today, that would need new scraping support in the Go
		/// fork. Rate-limited per (player, coordinate) so repeated probing from the same place doesn't
		/// trigger a fresh counter-spy wave every single time.
		/// </summary>
		private async Task SpyBackAtOrigin(Celestial attackedCelestial, Coordinate origin, AttackerFleet attack) {
			_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
			if (attackedCelestial.Ships.EspionageProbe == 0) {
				DoLog(LogLevel.Warning, "Could not spy attacker: no probes available.");
				return;
			}

			int probes = (int) _tbotInstance.InstanceSettings.Defender.SpyAttacker.Probes;
			int cooldownMinutes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender.SpyAttacker, "CooldownMinutes")
				? (int) _tbotInstance.InstanceSettings.Defender.SpyAttacker.CooldownMinutes : 60;
			int maxKnownCoordinates = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender.SpyAttacker, "MaxKnownCoordinates")
				? (int) _tbotInstance.InstanceSettings.Defender.SpyAttacker.MaxKnownCoordinates : 5;

			var targets = new List<Coordinate> { origin };
			try {
				var playersDb = await PlayersDatabase.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);
				playersDb.RecordSighting(attack.AttackerID, attack.AttackerName, origin.ToString());
				await playersDb.Save();

				foreach (var known in playersDb.GetKnownCoordinates(attack.AttackerID, attack.AttackerName)) {
					if (Coordinate.TryParse(known, out Coordinate knownCoord) && !targets.Any(t => t.IsSame(knownCoord)))
						targets.Add(knownCoord);
				}
				if (targets.Count > maxKnownCoordinates) {
					DoLog(LogLevel.Debug, $"Counter-spy: {attack.AttackerName} has {targets.Count} known coordinates, capping to {maxKnownCoordinates} (origin always included).");
					targets = targets.Take(maxKnownCoordinates).ToList();
				}
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"Could not load known coordinates for {attack.AttackerName}, spying only the origin: {e.Message}");
			}

			foreach (var target in targets) {
				string cooldownKey = $"{attack.AttackerID}:{target}";
				DateTime now = DateTime.UtcNow;
				if (_spyBackSentUntil.TryGetValue(cooldownKey, out DateTime sentUntil) && now < sentUntil) {
					DoLog(LogLevel.Debug, $"Counter-spy on {target} skipped: already spied within the last {cooldownMinutes}min.");
					continue;
				}
				_spyBackSentUntil[cooldownKey] = now.AddMinutes(cooldownMinutes);

				attackedCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Ships);
				await SendSpyProbes(attackedCelestial, target, probes);

				try {
					var galaxyInfo = await _ogameService.GetGalaxyInfo(target.Galaxy, target.System);
					var targetPlanet = galaxyInfo?.Planets?.SingleOrDefault(p => p != null && p.Coordinate.Position == target.Position);
					Coordinate sibling = null;
					if (target.Type == Celestials.Planet && targetPlanet?.Moon != null) {
						sibling = new Coordinate(target.Galaxy, target.System, target.Position, Celestials.Moon);
					} else if (target.Type == Celestials.Moon && targetPlanet != null) {
						sibling = new Coordinate(target.Galaxy, target.System, target.Position, Celestials.Planet);
					}
					if (sibling != null && !targets.Any(t => t.IsSame(sibling))) {
						attackedCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Ships);
						await SendSpyProbes(attackedCelestial, sibling, probes);
					}
				} catch (Exception e) {
					DoLog(LogLevel.Debug, $"Could not check/spy sibling celestial of {target}: {e.Message}");
				}
			}
		}

		private async Task SendSpyProbes(Celestial origin, Coordinate destination, int probes) {
			if (origin.Ships.EspionageProbe < probes) {
				DoLog(LogLevel.Warning, $"Could not spy {destination.ToString()}: not enough probes available.");
				return;
			}
			try {
				Ships ships = new() { EspionageProbe = probes };
				int fleetId = await _fleetScheduler.SendFleet(origin, ships, destination, Missions.Spy, Speeds.HundredPercent, new Resources(), _tbotInstance.UserData.userInfo.Class);
				Fleet fleet = _tbotInstance.UserData.fleets.Single(fleet => fleet.ID == fleetId);
				DoLog(LogLevel.Information, $"Spying {destination.ToString()} from {origin.ToString()} with {probes} probes. Arrival at {fleet.ArrivalTime.ToString()}");
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"Could not spy {destination.ToString()}: an exception has occurred: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			}
		}

		private async void HandleAttack(AttackerFleet attack) {
			if (_tbotInstance.UserData.celestials.Count() == 0) {
				DateTime time = await _tbotOgameBridge.GetDateTime();
				long interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Warning, "Unable to handle attack at the moment: bot is still getting account info.");
				DoLog(LogLevel.Information,  $"Next check at {newTime.ToString()}");
				return;
			}

			Celestial attackedCelestial = _tbotInstance.UserData.celestials.Unique().SingleOrDefault(planet => planet.HasCoords(attack.Destination));
			attackedCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Ships);

			// Anti-Bashing: if this attacker is someone AutoFarm has farmed before, mark them as retaliated
			// so AutoFarm permanently avoids them from now on (see PlayersDatabase / AutoFarmWorker).
			try {
				var playersDb = await PlayersDatabase.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);
				var existing = playersDb.Get(attack.AttackerID, attack.AttackerName);
				if (existing != null && existing.TimesFarmedByUs > 0) {
					bool isNewlyBlacklisted = playersDb.RecordRetaliation(attack.AttackerID, attack.AttackerName);
					await playersDb.Save();
					if (isNewlyBlacklisted) {
						DoLog(LogLevel.Critical, $"Anti-Bashing: player {attack.AttackerName} ({attack.AttackerID}), farmed by us {existing.TimesFarmedByUs}x before, just retaliated! Blacklisting them from AutoFarm permanently.");
						await _tbotInstance.SendTelegramMessage($"⚔️ Anti-Bashing: {attack.AttackerName} retaliou depois de sermos nós a farmar ele(a) {existing.TimesFarmedByUs}x. Bloqueado permanentemente do AutoFarm.");
					}
				}
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"Unable to check/update players database for anti-bashing: {e.Message}");
			}

			try {
				if ((_tbotInstance.InstanceSettings.Defender.WhiteList as long[]).Any()) {
					foreach (int playerID in (long[]) _tbotInstance.InstanceSettings.Defender.WhiteList) {
						if (attack.AttackerID == playerID) {
							DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: attacker {attack.AttackerName} whitelisted.");
							return;
						}
					}
				}
			} catch {
				DoLog(LogLevel.Warning, "An error has occurred while checking Defender WhiteList");
			}

			try {
				if (attack.MissionType == Missions.MissileAttack) {
					if ((bool) _tbotInstance.InstanceSettings.Defender.TelegramMessenger.Active) {
						await _tbotInstance.SendTelegramMessage($"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attack.Destination.ToString()} with IPM!");
					}
					DoLog(LogLevel.Information, $"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attack.Destination.ToString()} with IPM!");
					if (
						!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "DefendFromMissiles") ||
						(SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "DefendFromMissiles") && (bool) _tbotInstance.InstanceSettings.Defender.DefendFromMissiles)
					) {
						Celestial defenderCelestial = attackedCelestial;
						if (attackedCelestial.Coordinate.Type == Celestials.Moon) {
							 defenderCelestial = _tbotInstance.UserData.celestials.Unique().SingleOrDefault(planet => planet.HasCoords(new Coordinate {
								 Galaxy = attackedCelestial.Coordinate.Galaxy,
								 System = attackedCelestial.Coordinate.System,
								 Position = attackedCelestial.Coordinate.Position,
								 Type = Celestials.Planet
							}));
						}
						defenderCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Facilities);
						if (defenderCelestial.Facilities.MissileSilo >= 2) {
							defenderCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Defences);
							defenderCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Productions);
							if (defenderCelestial.Productions.Count == 0) {
								var availableSpace = defenderCelestial.Facilities.MissileSilo - defenderCelestial.Defences.AntiBallisticMissiles - (2 * defenderCelestial.Defences.InterplanetaryMissiles);
								defenderCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Resources);
								if (availableSpace > 0) {
									DoLog(LogLevel.Information, $"Building {availableSpace} AntiBallisticMissiles on {defenderCelestial.ToString()}");
									await _ogameService.BuildDefences(defenderCelestial, Buildables.AntiBallisticMissiles, availableSpace);
								}
								else {
									DoLog(LogLevel.Information, $"Unable to build AntiBallisticMissiles on {defenderCelestial.ToString()}: there is no space");
								}
							}
							else {
								DoLog(LogLevel.Information, $"Unable to build AntiBallisticMissiles on {defenderCelestial.ToString()}: a production is ongoing");
							}
						}
						else {
							DoLog(LogLevel.Information, $"No MissileSilo level >= 2 on {defenderCelestial.ToString()}");
						}
					}
					return;
				}
				if (attack.Ships != null && _tbotInstance.UserData.researches.EspionageTechnology >= 8) {
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "IgnoreProbes") && (bool) _tbotInstance.InstanceSettings.Defender.IgnoreProbes && attack.IsOnlyProbes()) {
						if (attack.MissionType == Missions.Spy) {
							DoLog(LogLevel.Information, "Attacker sent only Probes! Espionage action skipped.");
							await NotifySpyWatch(attack);
							if ((bool) _tbotInstance.InstanceSettings.Defender.SpyAttacker.Active)
								await SpyBackAtOrigin(attackedCelestial, attack.Origin, attack);
						} else {
							DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: only Espionage Probes.");
						}

						return;
					}
					if (
						(bool) _tbotInstance.InstanceSettings.Defender.IgnoreWeakAttack &&
						attack.Ships.GetFleetPoints() < (attackedCelestial.Ships.GetFleetPoints() / (int) _tbotInstance.InstanceSettings.Defender.WeakAttackRatio)
					) {
						DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: weak attack.");
						return;
					}
				} else {
					DoLog(LogLevel.Information, "Unable to detect fleet composition.");
				}
				if (
					(bool) _tbotInstance.InstanceSettings.Defender.IgnoreAttackIfIHave.Active &&
					attackedCelestial.Resources.TotalResources < (long) _tbotInstance.InstanceSettings.Defender.IgnoreAttackIfIHave.MinResourcesToSave &&
					(attackedCelestial.Ships.GetFleetPoints() *1000) < (long) _tbotInstance.InstanceSettings.Defender.IgnoreAttackIfIHave.MinFleetToSave
				) {
					DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: it's not worth it.");
					return;
				}
			} catch {
				DoLog(LogLevel.Warning, "An error has occurred while checking attacker fleet composition");
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.TelegramMessenger.Active) {
				await _tbotInstance.SendTelegramMessage($"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attack.Destination.ToString()} arriving at {attack.ArrivalTime.ToString()}");
				if (attack.Ships != null)
					await Task.Delay(1000, _ct);
				await _tbotInstance.SendTelegramMessage($"The attack is composed by: {attack.Ships.ToString()}");
			}
			DoLog(LogLevel.Warning, $"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attackedCelestial.ToString()} arriving at {attack.ArrivalTime.ToString()}");
			if (attack.Ships != null)
				await Task.Delay(1000, _ct);
			DoLog(LogLevel.Warning, $"The attack is composed by: {attack.Ships.ToString()}");

			if ((bool) _tbotInstance.InstanceSettings.Defender.SpyAttacker.Active) {
				await SpyBackAtOrigin(attackedCelestial, attack.Origin, attack);
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.MessageAttacker.Active) {
				try {
					if (attack.AttackerID != 0) {
						Random random = new();
						string[] messages = _tbotInstance.InstanceSettings.Defender.MessageAttacker.Messages;
						string message = messages.ToList().Shuffle().First();
						DoLog(LogLevel.Information, $"Sending message \"{message}\" to attacker {attack.AttackerName}");
						try {
							await _ogameService.SendMessage(attack.AttackerID, message);
							DoLog(LogLevel.Information, "Message succesfully sent.");
						} catch {
							DoLog(LogLevel.Warning, "Unable send message.");
						}
					} else {
						DoLog(LogLevel.Warning, "Unable send message.");
					}

				} catch (Exception e) {
					DoLog(LogLevel.Error, $"Could not message attacker: an exception has occurred: {e.Message}");
					DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				}
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.Autofleet.Active) {
				try {
					// If the impact is far enough away that Defender's own next scheduled check will
					// happen before it (worst case: CheckIntervalMax from now), there's no rush - skip the
					// fleet save this pass and let a later, closer-to-impact check re-evaluate instead of
					// moving the fleet prematurely (e.g. in case the attack gets recalled/cancelled).
					bool delayIfLater = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender.Autofleet, "DelayFleetSaveIfImpactOccurLaterThanNextCheck")
						&& (bool) _tbotInstance.InstanceSettings.Defender.Autofleet.DelayFleetSaveIfImpactOccurLaterThanNextCheck;
					if (delayIfLater) {
						long nextCheckWorstCaseSeconds = (long) _tbotInstance.InstanceSettings.Defender.CheckIntervalMax * 60;
						if (attack.ArriveIn > nextCheckWorstCaseSeconds) {
							DoLog(LogLevel.Information, $"Impact in {attack.ArriveIn}s is later than Defender's next check (up to {nextCheckWorstCaseSeconds}s) - delaying fleet save, will re-evaluate next check.");
							return;
						}
					}

					var minFlightTime = attack.ArriveIn + (attack.ArriveIn / 100 * 30) + (RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds) / 1000);
					await _fleetScheduler.AutoFleetSave(attackedCelestial, false, minFlightTime);
				} catch (Exception e) {
					DoLog(LogLevel.Error, $"Could not fleetsave: an exception has occurred: {e.Message}");
					DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				}
			}
		}
	}
}
