using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using JiraLibM.Entities.Issues;
using JiraLibM.Entities.Transitions;

namespace JiraLibM.Entities.Searching
{
    /// <summary>
    /// A class representing a JIRA REST search response
    /// </summary>
    public class SearchResponse
    {
        [JsonProperty("expand")]
        public string Expand { get; set; }

        [JsonProperty("startAt")]
        public int StartAt { get; set; }

        [JsonProperty("maxResults")]
        public int MaxResults { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("issues")]
        public List<Issue> IssueList { get; set; }

        [JsonProperty("transitions")]
        public List<Transition> transitionsDescriptions { get; set; }
    }
}
