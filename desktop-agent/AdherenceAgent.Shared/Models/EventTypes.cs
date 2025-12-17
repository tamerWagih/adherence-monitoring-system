namespace AdherenceAgent.Shared.Models;

public static class EventTypes
{
    public const string Login = "LOGIN";
    public const string Logoff = "LOGOFF";
    public const string IdleStart = "IDLE_START";
    public const string IdleEnd = "IDLE_END";
    public const string WindowChange = "WINDOW_CHANGE";
    public const string ApplicationStart = "APPLICATION_START";
    public const string ApplicationEnd = "APPLICATION_END";
    public const string BreakStart = "BREAK_START";
    public const string BreakEnd = "BREAK_END";
    public const string CallStart = "CALL_START";
    public const string CallEnd = "CALL_END";
    
    // Day 8: Advanced Activity Detection
    public const string TeamsMeetingStart = "TEAMS_MEETING_START";
    public const string TeamsMeetingEnd = "TEAMS_MEETING_END";
    public const string TeamsChatActive = "TEAMS_CHAT_ACTIVE";
    public const string BrowserTabChange = "BROWSER_TAB_CHANGE";
    
    // Day 9: Client Website & Calling App Detection
    public const string ClientWebsiteAccess = "CLIENT_WEBSITE_ACCESS";
    public const string CallingAppStart = "CALLING_APP_START";
    public const string CallingAppEnd = "CALLING_APP_END";
    public const string CallingAppInCall = "CALLING_APP_IN_CALL";
}

