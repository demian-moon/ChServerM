using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JiraLibM.Entities.Transitions
{
    public class Solve
    {
        [JsonProperty("transition")]
        public Transition transition { get; set; }
    }
}
