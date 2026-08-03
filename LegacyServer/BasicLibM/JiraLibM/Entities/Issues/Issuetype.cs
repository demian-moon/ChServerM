using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace JiraLibM.Entities.Issues
{
    public class Issuetype : BaseEntity
    {
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("subtask")]
        public bool subtask { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public int Id { get; set; }
    }
}
