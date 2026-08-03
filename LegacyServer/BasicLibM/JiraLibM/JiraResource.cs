using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JiraLibM
{
    /// <summary>
    /// An enumeration representing the various resources that can
    /// be used in a JIRA REST request
    /// </summary>
    public enum JiraResource
    {
        project,
        search,
        issue,
        transitions
    }
}
