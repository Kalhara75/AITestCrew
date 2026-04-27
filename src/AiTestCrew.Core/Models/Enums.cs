using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AiTestCrew.Core.Models;

public enum TestStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Error,
    Skipped,
    /// <summary>
    /// Objective/step has a post-delivery verification queued for later execution.
    /// The run does not finalise until all awaited verifications complete or exceed their deadline.
    /// </summary>
    AwaitingVerification
}

public enum TestTargetType
{
    API_REST,
    API_GraphQL,
    UI_Web_MVC,
    UI_Web_Blazor,
    UI_Desktop_WinForms,
    BackgroundJob_Hangfire,
    MessageBus,
    Database,
    AseXml_Generate,
    AseXml_Deliver,
    EndToEnd,
    /// <summary>
    /// SQL Server database check — runs a read-only SELECT and compares the
    /// result against an expected row count or column-value dictionary. Used
    /// as a post-step on any parent step type (UI, API, aseXML delivery).
    /// </summary>
    Db_SqlServer
}

public enum TestPriority
{
    Low,
    Normal,
    High,
    Critical
}
