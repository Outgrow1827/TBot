using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Model;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Services {

	// Data required by TBotMain instances
	public class UserData {
		public Server serverInfo = new();
		public ServerData serverData;
		public UserInfo userInfo;
		public AllianceClass allianceClass;
		public List<Celestial> celestials;
		public List<Fleet> fleets;
		public List<AttackerFleet> attacks;
		public Slots slots;
		public Researches researches;

		public List<FleetSchedule> scheduledFleets;
		// Keyed by FarmTarget.GetKey() (coordinate string) - O(1) lookup/update instead of a linear scan
		// over every known farm target on every planet scanned by AutoFarmWorker (this list only grows
		// over a 24/7 run, so a List<> here degrades towards O(n^2) over time).
		public Dictionary<string, FarmTarget> farmTargets = new();
		public Dictionary<Coordinate, DateTime> discoveryBlackList;
		public float lastDOIR;
		public float nextDOIR;
		public Staff staff;
		public bool isSleeping = false;

		// Fleet IDs the bot itself sent via FleetScheduler.SendFleet() (attacks, spies, everything) - lets
		// AutoFarmWorker's manual-activity detection tell apart fleets/reports that came from the player
		// acting manually (in the browser, outside the bot) from the bot's own actions.
		public HashSet<int> BotSentFleetIds = new();
	}

	// Data used by TelegramMessenger binded to TBotMain
	public class TelegramUserData {
		public Celestial CurrentCelestial;			// Willingly left to null
		public Celestial CurrentCelestialToSave;	// Willingly left to null
		public Missions Mission = Missions.None;
	}
}
