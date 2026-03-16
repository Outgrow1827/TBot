using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Models {
	public class LFBonusesMoons {
		public float Fields { get; set; }
		public float Size { get; set; }
        public float Chance { get; set; }

		public LFBonusesMoons(float fields = 0, float size = 0, float chance = 0) {
			Fields = fields;
			Size = size;
			Chance = chance;
		}
    }
}
