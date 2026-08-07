using Newtonsoft.Json;

namespace TBot.Ogame.Infrastructure.Models
{
    public class JumpGateResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("rechargeCountdown")]
        public long RechargeCountdown { get; set; }

        [JsonIgnore]
        public string Error { get; set; }
    }
}
