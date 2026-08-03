using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using JiraLibM.Entities.Projects;

namespace JiraLibM.Entities.Issues
{
    /// <summary>
    /// Represents a Fields JSON object
    /// </summary>
    /// exact Jason object
    /// <remarks>
    /// "fields" : {
    ///	    "summary" : "Some summary",
    ///	    "status" : {
    ///	    	...
    ///	    },
    ///	    "assignee" : {
    ///	    	...
    ///	    }
    /// }    
    /// </remarks>
    public class Fields
    {
        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("status")] 
        public Status Status { get; set; }

        [JsonProperty("assignee")]
        public Assignee Assignee { get; set; }

        [JsonProperty("project")]
        public ProjectDescription project { get; set; }

        [JsonProperty("issuetype")]
        public Issuetype issuetype { get; set; }

        [JsonProperty("worklog")]
        public Worklog worklog { get; set; }
    }
}
