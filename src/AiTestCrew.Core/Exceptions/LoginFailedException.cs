namespace AiTestCrew.Core.Exceptions;

/// <summary>
/// Thrown by <c>LoginTokenProvider.LoginAsync</c> when the configured
/// <c>AuthUsername</c> / <c>AuthPassword</c> pair is rejected by the SEC API
/// (or whichever backing login endpoint the stack is configured to use).
///
/// Distinct from <see cref="AuthRequiredException"/> on purpose: this is the
/// "credentials in <c>appsettings.json</c> are wrong / locked / rotated" case.
/// The dashboard's reactive refresh banner can't help — for the API surface
/// it would just re-call the same endpoint with the same bad creds. The
/// remediation is human: fix the creds (or unlock the account) and re-run.
/// </summary>
public sealed class LoginFailedException : Exception
{
    public string LoginUrl { get; }
    public string Username { get; }
    public int HttpStatusCode { get; }
    public string ResponseSnippet { get; }

    public LoginFailedException(
        string loginUrl,
        string username,
        int httpStatusCode,
        string responseSnippet,
        Exception? innerException = null)
        : base(
            $"Login failed: {httpStatusCode} for user '{username}' at {loginUrl} — {responseSnippet}",
            innerException)
    {
        LoginUrl = loginUrl;
        Username = username;
        HttpStatusCode = httpStatusCode;
        ResponseSnippet = responseSnippet;
    }
}
