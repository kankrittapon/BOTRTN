using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SettingsManager
{
    private const string SettingsFileName = "UserSettings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static RunnerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return CreateDefault();

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<RunnerSettings>(json, JsonOptions);
            return Normalize(settings) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static void Save(RunnerSettings settings)
    {
        var normalized = Normalize(settings) ?? CreateDefault();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath) ?? ".");
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    private static RunnerSettings CreateDefault()
    {
        var defaults = Normalize(new RunnerSettings())!;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath) ?? ".");
        var json = JsonSerializer.Serialize(defaults, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
        return defaults;
    }

    private static RunnerSettings? Normalize(RunnerSettings? settings)
    {
        settings ??= new RunnerSettings();

        if (string.IsNullOrWhiteSpace(settings.TargetUrl))
            settings.TargetUrl = "https://www.facebook.com/";

        if (string.IsNullOrWhiteSpace(settings.ScreenshotPath))
            settings.ScreenshotPath = "artifacts/screenshot.png";

        if (!BrowserChannels.Supported.Contains(settings.Browser))
            settings.Browser = BrowserChannels.Chrome;

        if (settings.Timeout <= 0)
            settings.Timeout = 30000;

        settings.Profiles ??= new List<BrowserProfileSettings>();
        if (settings.Profiles.Count == 0)
            settings.Profiles.Add(CreateDefaultProfile("Default", settings.Profiles.Count));

        NormalizeProfiles(settings);

        settings.Tasks ??= new List<ProfileTaskSettings>();
        if (settings.Tasks.Count == 0)
            settings.Tasks.Add(CreateDefaultTask(settings.Profiles[0]));

        NormalizeTasks(settings);

        if (string.IsNullOrWhiteSpace(settings.SelectedProfile) ||
            !settings.Profiles.Any(p => string.Equals(p.Name, settings.SelectedProfile, StringComparison.Ordinal)))
        {
            settings.SelectedProfile = settings.Profiles[0].Name;
        }

        return settings;
    }

    private static void NormalizeProfiles(RunnerSettings settings)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in settings.Profiles)
        {
            profile.Credentials ??= new CredentialSettings();
            profile.Proxy ??= new ProxySettings();

            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = GenerateUniqueProfileName(settings.Profiles, "Profile");

            if (!seenNames.Add(profile.Name))
            {
                profile.Name = GenerateUniqueProfileName(settings.Profiles, profile.Name);
                seenNames.Add(profile.Name);
            }

            if (string.IsNullOrWhiteSpace(profile.UserDataDirName))
                profile.UserDataDirName = $"botRTN_{Sanitize(profile.Name)}";
        }
    }

    private static void NormalizeTasks(RunnerSettings settings)
    {
        var profileNames = new HashSet<string>(settings.Profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var seenTaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in settings.Tasks)
        {
            if (task.Id == Guid.Empty)
                task.Id = Guid.NewGuid();

            if (string.IsNullOrWhiteSpace(task.Name))
                task.Name = GenerateUniqueTaskName(settings.Tasks, "Task");

            if (!seenTaskNames.Add(task.Name))
            {
                task.Name = GenerateUniqueTaskName(settings.Tasks, task.Name);
                seenTaskNames.Add(task.Name);
            }

            if (string.IsNullOrWhiteSpace(task.ProfileName) || !profileNames.Contains(task.ProfileName))
                task.ProfileName = settings.SelectedProfile;

            if (task.RunMode == TaskRunMode.Delay && (task.Delay is null || task.Delay.Value <= TimeSpan.Zero))
                task.Delay = TimeSpan.FromMinutes(1);

            if (task.RunMode == TaskRunMode.DailyTime && task.RunAtTime is null)
                task.RunAtTime = TimeSpan.FromHours(9);

            if (task.RunMode == TaskRunMode.Immediate)
            {
                task.Delay = null;
                task.RunAtTime = null;
            }
        }
    }

    private static BrowserProfileSettings CreateDefaultProfile(string baseName, int index)
    {
        var name = index == 0 ? baseName : $"{baseName} {index + 1}";
        return new BrowserProfileSettings
        {
            Name = name,
            UserDataDirName = $"botRTN_{Sanitize(name)}",
            Credentials = new CredentialSettings(),
            Proxy = new ProxySettings()
        };
    }

    private static ProfileTaskSettings CreateDefaultTask(BrowserProfileSettings profile)
    {
        return new ProfileTaskSettings
        {
            Name = "Task 1",
            ProfileName = profile.Name,
            Enabled = true,
            RunMode = TaskRunMode.Immediate,
            UseCredentials = true
        };
    }

    private static string SettingsFilePath =>
        Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    private static string GenerateUniqueProfileName(IEnumerable<BrowserProfileSettings> profiles, string baseName)
    {
        var sanitized = string.IsNullOrWhiteSpace(baseName) ? "Profile" : baseName.Trim();
        var candidate = sanitized;
        var suffix = 1;
        while (profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{sanitized} {++suffix}";
        }

        return candidate;
    }

    private static string GenerateUniqueTaskName(IEnumerable<ProfileTaskSettings> tasks, string baseName)
    {
        var sanitized = string.IsNullOrWhiteSpace(baseName) ? "Task" : baseName.Trim();
        var candidate = sanitized;
        var suffix = 1;
        while (tasks.Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{sanitized} {++suffix}";
        }

        return candidate;
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Default";

        var cleaned = Regex.Replace(name.Trim(), @"[^\w\-]+", "_");
        return string.IsNullOrWhiteSpace(cleaned) ? "Default" : cleaned;
    }

    public static RunnerSettings Clone(RunnerSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var clone = JsonSerializer.Deserialize<RunnerSettings>(json, JsonOptions);
        return Normalize(clone) ?? new RunnerSettings();
    }

}

