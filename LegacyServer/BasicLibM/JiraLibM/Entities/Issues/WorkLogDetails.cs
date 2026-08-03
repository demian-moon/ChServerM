using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace JiraLibM.Entities.Issues
{
    public class WorkLogDetails
    {

        [JsonProperty("self")]
        public string self { get; set; }
                
        [JsonProperty("comment")]
        public string comment { get; set; }

        [JsonProperty("started")]
        public System.Nullable<System.DateTime> started { get; set; }        

        [JsonProperty("timeSpent")]
        public string timeSpent { get; set; }

        [JsonProperty("timeSpentSeconds")]
        public int timeSpentSeconds { get; set; }

        [JsonProperty("author")]
        public WorkAuthor author { get; set; }

        [JsonProperty("issueId")]   // 프로젝트로 된 LOD-xxx가 아님으로 주의!!!!!
        public string issueId { get; set; }

        [JsonProperty("id")]
        public string id { get; set; }

        public WorkLogDetails(string comment, DateTime? workedDate, DateTime endDate, string authorName, string workId)
        {
            this.comment = comment;
            this.timeSpentSeconds = (int)(endDate - workedDate.Value).TotalSeconds;
            this.started = workedDate.Value;
            //string timeSpentStr = string.Format("{0}h {1}m", timeSpentSeconds / 3600, timeSpentSeconds / 60);          // 쓰면 안됨 - 에러남   
            //this.timeSpent = timeSpentStr;
            this.id = workId;                        
            this.author = new WorkAuthor(authorName);        
        }
        
        public WorkLogDetails() { }


    }

    public class WorkLogDetailsForUpdate
    {

        [JsonProperty("self")]
        public string self { get; set; }

        [JsonProperty("comment")]
        public string comment { get; set; }

        [JsonProperty("started")]
        public string started { get; set; }

        [JsonProperty("timeSpent")]
        public string timeSpent { get; set; }

        [JsonProperty("timeSpentSeconds")]
        public int timeSpentSeconds { get; set; }

        [JsonProperty("author")]
        public WorkAuthor author { get; set; }

        [JsonProperty("id")]
        public string id { get; set; }

        public WorkLogDetailsForUpdate(WorkLogDetails wkd)
        {
            this.comment = wkd.comment;            
            this.started = wkd.started.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffzzff");
            this.timeSpentSeconds = wkd.timeSpentSeconds;
            //this.timeSpent = wkd.timeSpent;
            this.author = new WorkAuthor(wkd.author.name);
        }

        public WorkLogDetailsForUpdate() { }

    }
        

    public class WorkAuthor
    {
        [JsonProperty("name")]
        public string name { get; set; }

        [JsonProperty("displayName")]
        public string displayName { get; set; }

        [JsonProperty("self")]
        public string self { get; set; }

        [JsonProperty("active")]
        public bool active { get; set; }

        public WorkAuthor(string name)
        {
            this.name = name;
            displayName = name;
        }

        public WorkAuthor() { }
    }
}
