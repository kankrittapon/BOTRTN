using System;
using System.Collections.Generic;

public sealed class RunnerSettings
{
    public string TargetUrl { get; set; } = "https://www.facebook.com/";
    public bool Headless { get; set; }
    public string Browser { get; set; } = BrowserChannels.Chrome;
    public string ScreenshotPath { get; set; } = "artifacts/screenshot.png";
    public int Timeout { get; set; } = 30000;
    public string SelectedProfile { get; set; } = "Default";

    public List<BrowserProfileSettings> Profiles { get; set; } = new();
    public List<ProfileTaskSettings> Tasks { get; set; } = new();
    public LoginSettings Login { get; set; } = new();
}

public sealed class BrowserProfileSettings
{
    public string Name { get; set; } = "Default";
    public string UserDataDirName { get; set; } = "botRTN_Default";
    public CredentialSettings Credentials { get; set; } = new();
    public ProxySettings Proxy { get; set; } = new();

    public override string ToString() => Name;
}

public sealed class ProfileTaskSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Task 1";
    public string ProfileName { get; set; } = "Default";
    public bool Enabled { get; set; } = true;
    public TaskRunMode RunMode { get; set; } = TaskRunMode.Immediate;
    public TimeSpan? Delay { get; set; }
    public TimeSpan? RunAtTime { get; set; }
    public bool RepeatDaily { get; set; } = true;
    public bool UseCredentials { get; set; } = true;
    public string? TargetUrlOverride { get; set; }
    public string? ScreenshotPathOverride { get; set; }

    public override string ToString() => Name;
}

public enum TaskRunMode
{
    Immediate = 0,
    Delay = 1,
    DailyTime = 2
}

public sealed class ProxySettings
{
    public bool Enabled { get; set; }
    public string? Server { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class CredentialSettings
{
    public string? User { get; set; }
    public string? Pass { get; set; }
}

public sealed class LoginSettings
{
    public string Url { get; set; } = "https://www.facebook.com/login";
    public string UserSelector { get; set; } = "input[name='email']";
    public string PassSelector { get; set; } = "input[name='pass']";
    public string SubmitSelector { get; set; } = "button[name='login'], button[type='submit']";
    public string LoggedInCheckSelector { get; set; } = "div[role='feed']";
    public string? AfterLoginWaitSelector { get; set; } = "div[role='feed']";
    public string? AfterLoginUrl { get; set; } = "https://www.facebook.com/";
    public string? TwoFactorSelector { get; set; } = "input#approvals_code, input[name='approvals_code'], input[name='otp']";
    public int WaitTimeout { get; set; } = 30000;
}

public static class BrowserChannels
{
    public const string Chrome = "chrome";
    public const string Edge = "msedge";

    public static readonly IReadOnlyList<string> Supported = new[] { Chrome, Edge };
}
