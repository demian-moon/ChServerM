using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using JiraLibM.Entities.Projects;
using Newtonsoft.Json;
using JiraLibM.Entities.Issues;
using JiraLibM.Entities.Searching;
using JiraLibM.Entities.Transitions;
using System.Threading.Tasks;
using System.Net.Http;
using System.Runtime.CompilerServices;



namespace JiraLibM
{
    public interface IWorklogIntiable<T> where T : IWorklogIntiable<T>, new()
    {
        T InitWithWorklogDetails(string issueId, WorkLogDetails detail);
    }

    public class JiraManager
    {
        
        private const string m_BaseUrl = "https://jiralive.nexon.com/rest/api/2/";        
        private string m_userName;
        private string m_Password;

        public JiraManager(string username, string password)
        {
            m_userName = username;
            m_Password = password;
        }

        public async Task<Issue> GetIssueById(string issueId)
        {
            string issueString = await RunQuery(JiraResource.issue, issueId).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<Issue>(issueString);
        }

       

        public string GetUserName()
        {
            
            return m_userName;
        }


        public string GetUserNameForQuery()
        {
            var userName = m_userName.Replace("@", "\\u0040");
            return userName;
        }

        public async Task<List<Issue>> GetIssueWorklogOnDate(string projectKey, DateTime date, DateTime searchAfterYear, List<string> fieldString = null)
        {

            
            string formattedDate = date.ToString("yyyy/MM/dd");
            string jql = $"project={projectKey} AND created >={searchAfterYear.ToString("yyyy/MM/dd")} AND worklogAuthor={m_userName} AND worklogDate={formattedDate}";
                        
            if (fieldString == null)
                    fieldString = new List<string>();

            List<Issue> issueList = await GetIssues(jql, fieldString).ConfigureAwait(false);

            return issueList;           
            
        }

        public async Task<List<Issue>> GetIssueKeyWorklogOnDate(string projectKey, DateTime date, DateTime searchAfterYear)
        {
            List<string> fieldString = new List<string>() { "key" };
            return await GetIssueWorklogOnDate(projectKey, date, searchAfterYear, fieldString).ConfigureAwait(false);
        }


        public async Task<List<T>> GetWorklogOnDate<T>(string projectKey, DateTime date, DateTime searchAfterYear) where T : IWorklogIntiable<T>, new()
        {
            List<T> excelWorklogList = new List<T>();
            List<string> issueIdList = new List<string>();
            List<WorkLogDetails> worklogDetailList= new List<WorkLogDetails>();

            var issueKeyList = await GetIssueKeyWorklogOnDate(projectKey, date, searchAfterYear).ConfigureAwait(false); // 이슈에서 키만 얻어오기

            if (issueKeyList.Count <= 0)
                return excelWorklogList;
                        
            var enIssue = issueKeyList.GetEnumerator();
            while (enIssue.MoveNext())
            {
                var issue = enIssue.Current;
                string issueId = issue.Key.ToString();

                var worklog = await GetAllWorklogsInIssue(issueId).ConfigureAwait(false);

                var wklogDetailList = worklog.WorkLogs;
                wklogDetailList = wklogDetailList.Where(x => x.author.name == GetUserName() && x.started.Value.Date == date.Date).ToList(); // author 나인것만 

                for (int i=0; i<wklogDetailList.Count; i++)
                    issueIdList.Add(issueId);

                worklogDetailList.AddRange(wklogDetailList);
            }


            // 정렬 오름차순
            worklogDetailList.Sort((a, b) =>
            {
                if (a.started > b.started) return 1;
                else if (a.started == b.started) return 0;
                else return -1;
            });

            for (int i = 0; i < worklogDetailList.Count; i++)
            {
                T mWorklog = new T();
                mWorklog.InitWithWorklogDetails(issueIdList[i], worklogDetailList[i]);
                excelWorklogList.Add(mWorklog);
            }
                       
            return excelWorklogList;
        }


        public async Task<Worklog> GetAllWorklogsInIssue(string issueID)
        {
            //fields = fields ?? new string() {"author"}

            string jql = string.Format("{0}/worklog", issueID);
            
            //SearchRequest request = new SearchRequest();
            //request.Fields = fields;
            //request.JQL = jql;
            //request.MaxResults = maxResult;
            //request.StartAt = startAt;

            string workLogsString = await RunQuery(JiraResource.issue, jql).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<Worklog>(workLogsString);
        }

        public async Task<List<ProjectDescription>> GetProjects()
        {
            List<ProjectDescription> projects = new List<ProjectDescription>();
            string projectsString = await RunQuery(JiraResource.project).ConfigureAwait(false);

            return JsonConvert.DeserializeObject<List<ProjectDescription>>(projectsString);
        }

        public async Task<string> AddWorklog(string issueId, WorkLogDetails detail)
        {            
            string arguments = string.Format("{0}/worklog", issueId);

            WorkLogDetailsForUpdate updateDetail = new WorkLogDetailsForUpdate(detail);                        

            string data = JsonConvert.SerializeObject(updateDetail);
            string result = await RunQuery(JiraResource.issue, arguments, data, "POST").ConfigureAwait(false);
            WorkLogDetails log = JsonConvert.DeserializeObject<WorkLogDetails>(result);
            return log.id.ToString();  // 로그의 아이디임
        }

        public async Task<string> UpdateWorklog(string issueId, WorkLogDetails detail)
        {   
            string worklogId = detail.id;
            string arguments = string.Format("{0}/worklog/{1}", issueId, worklogId);
            WorkLogDetailsForUpdate updateDetail = new WorkLogDetailsForUpdate(detail);

            string data = JsonConvert.SerializeObject(updateDetail);
            string result = await RunQuery(JiraResource.issue, arguments, data, "PUT").ConfigureAwait(false);
            WorkLogDetails log = JsonConvert.DeserializeObject<WorkLogDetails>(result);
            return log.id.ToString();  // 로그의 아이디임
        }

        public async Task<string> DeleteWorklog(string issueId, string worklogId)
        {
            string arguments = string.Format("{0}/worklog/{1}", issueId, worklogId);
            string result = await RunQuery(JiraResource.issue, arguments, null, "DELETE").ConfigureAwait(false);
            return result;
        }

        public async Task<SearchResponse> getTransitions(string issueID)
        {
            string jql = string.Format("{0}/transitions", issueID);
            string data = await RunQuery(JiraResource.issue, jql).ConfigureAwait(false);
            SearchResponse response = JsonConvert.DeserializeObject<SearchResponse>(data);
            return response;
        }

        public async Task resolveIssue(string issueID, Solve tr)
        {
            string jql = string.Format("{0}/transitions", issueID);
            string data = JsonConvert.SerializeObject(tr);
            string result = await RunQuery(JiraResource.issue,jql, data: data, method: "POST").ConfigureAwait(false);
        }

        private async Task<List<Transition>> GetTransitionsList(string jql, List<string> fields = null, int startAt = 0, int maxResult = 10)
        {
            fields = fields ?? new List<string> { "id", "name" };

            SearchRequest request = new SearchRequest();
            request.Fields = fields;
            request.JQL = jql;
            request.MaxResults = maxResult;
            request.StartAt = startAt;

            string data = JsonConvert.SerializeObject(request);
            string result = await RunQuery(JiraResource.issue, jql, data: data, method: "GET").ConfigureAwait(false);

            SearchResponse response = JsonConvert.DeserializeObject<SearchResponse>(result);

            return response.transitionsDescriptions;
        }



        public async Task<List<Issue>> GetEmployeeOpenIssues(string username)
        {
            username = username.Replace("@", "\\u0040");
            string jql = "assignee = " + username + " AND Resolution=Unresoved";
            List<Issue> issueList = await GetIssues(jql).ConfigureAwait(false);
            return issueList;
        }

        private async Task<List<Issue>> GetIssues(string jql, List<string> fields = null, int startAt = 0, int maxResult = 1000)
        {
            fields = fields ?? new List<string> { "summary", "status", "assignee", "project", "issuetype", "worklog" };

            SearchRequest request = new SearchRequest();                        
            request.Fields = fields;
            request.JQL = jql;
            request.MaxResults = maxResult;
            request.StartAt = startAt;

            string data = JsonConvert.SerializeObject(request);
            string result = await RunQuery(JiraResource.search, data: data, method: "POST").ConfigureAwait(false);

            SearchResponse response = JsonConvert.DeserializeObject<SearchResponse>(result);

            return response.IssueList;
        }



        /// <summary>
        /// Runs a query towards the JIRA REST api
        /// </summary>
        /// <param name="resource">The kind of resource to ask for</param>
        /// <param name="argument">Any argument that needs to be passed, such as a project key</param>
        /// <param name="data">More advanced data sent in POST requests</param>
        /// <param name="method">Either GET or POST</param>
        /// <returns></returns>
        protected async Task<string> RunQuery(JiraResource resource, string argument = null, string data = null,string method = "GET")
        {
            string url = string.Format("{0}{1}/", m_BaseUrl, resource.ToString());

            if (argument != null)
            {
                url = string.Format("{0}{1}", url, argument);
            }

            HttpWebRequest request = WebRequest.Create(url) as HttpWebRequest;
            request.ContentType = "application/json";
            request.Method = method;
            request.Accept = "application/json";

            string base64Credentials = GetEncodedCredentials();
            request.Headers.Add("Authorization", "Basic " + base64Credentials);
            
            if (data != null)
            {
                using (StreamWriter writer = new StreamWriter(await request.GetRequestStreamAsync()))
                {
                    await writer.WriteAsync(data).ConfigureAwait(false);
                }
            }
            
            WebResponse response = await (Task<WebResponse>)request.GetResponseAsync();
                                    

            string result = string.Empty;            
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                result = await reader.ReadToEndAsync().ConfigureAwait(false);
                
            }

            return result;
        }

        private string GetEncodedCredentials()
        {
            string mergedCredentials = string.Format("{0}:{1}", m_userName, m_Password);
            byte[] byteCredentials = UTF8Encoding.UTF8.GetBytes(mergedCredentials);
            return Convert.ToBase64String(byteCredentials);
        }

    }


    public class JiraLogin
    {
        const string QUERY_URL_FORMAT = "https://{0}/rest/api/2/{1}";
        string _baseUrl;
        CookieContainer _cookies;
        HttpClient _httpClient;

        public async Task<bool> Login(string userId, string password)
        {
            string url = string.Format(QUERY_URL_FORMAT, "jiralive.nexon.com", "myself");
            string authHeader = CreateBasicAuth(userId, password);

            HttpClientHandler handler = new HttpClientHandler();
            _cookies = new CookieContainer();
            handler.CookieContainer = _cookies;

            HttpClient hc = new HttpClient(handler);

            hc.DefaultRequestHeaders.Add("Authorization", authHeader);

            HttpResponseMessage hrm = await hc.GetAsync(url).ConfigureAwait(false);
            if (hrm.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return false;
            }

            _baseUrl = string.Format(QUERY_URL_FORMAT, "jiralive.nexon.com", "");
            _httpClient = hc;

            return true;
        }

        private string CreateBasicAuth(string userId, string password)
        {
            string text = userId + ":" + password;
            byte[] buf = Encoding.UTF8.GetBytes(text);
            return "Basic " + Convert.ToBase64String(buf);
        }
    }
}
