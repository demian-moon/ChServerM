using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using JiraLibM.Entities.Misc;

namespace JiraLibM.Entities.Projects
{
    /// <summary>
    /// A class representing a project descriptin in JIRA
    /// </summary>
    public class ProjectDescription 
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("avatarUrls")]
        public AvatarUrls AvatarUrls { get; set; }
    }
}
