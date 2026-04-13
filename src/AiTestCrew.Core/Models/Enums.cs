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
    Skipped
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
    EndToEnd
}

public enum TestPriority
{
    Low,
    Normal,
    High,
    Critical
}
