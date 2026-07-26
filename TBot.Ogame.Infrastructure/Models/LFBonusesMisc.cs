using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBonusesMisc {
		public float PhalanxRange { get; set; }

		public LFBonusesMisc(float phalanxrange = 0) {
			PhalanxRange = phalanxrange;
		}
    }
}
