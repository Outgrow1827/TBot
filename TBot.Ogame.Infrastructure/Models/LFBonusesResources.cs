using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBonusesResources {
		public float ResourcesExpedition { get; set; }
		public LFBonusesResources(float resourcesexpedition = 0) {
			ResourcesExpedition = resourcesexpedition;
		}
	}
}
