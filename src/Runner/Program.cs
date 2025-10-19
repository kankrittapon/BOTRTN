
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Playwright;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = SettingsManager.Load();
        var form = new SettingsForm(settings);

        form.RunRequested += async clonedSettings =>
        {
            var tasksToRun = clonedSettings.Tasks.Where(t => t.Enabled).ToList();
            form.InitializeTaskStatuses(tasksToRun);

            if (tasksToRun.Count == 0)
            {
                form.ShowStatusMessage("ไม่มี Task ที่เปิดใช้งาน");
                return;
            }

            try
            {
                await RunTasksAsync(clonedSettings, form, tasksToRun);
                form.ShowStatusMessage("งานทั้งหมดเสร็จแล้ว");
            }
            catch (LoginFailedException ex)
            {
                form.ShowStatusMessage($"เกิดข้อผิดพลาด: {ex.Message}");
            }
            catch (Exception ex)
            {
                form.ShowStatusMessage($"Unexpected error: {ex.Message}");
                MessageBox.Show(ex.ToString(), "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        Application.Run(form);
    }
    private static async Task RunTasksAsync(RunnerSettings settings, SettingsForm form, List<ProfileTaskSettings> tasksToRun)
    {
        foreach (var task in tasksToRun)
        {
            if (task.RunMode == TaskRunMode.DailyTime && !task.RepeatDaily && task.RunAtTime is TimeSpan oneShotTime && DateTime.Now.TimeOfDay > oneShotTime)
            {
                form.UpdateTaskStatus(task.Id, "ข้าม (เวลาเกินที่กำหนดแล้ว)");
                Console.WriteLine($"⏭️ ข้าม Task '{task.Name}' เพราะเวลาเกินที่ตั้งไว้");
                continue;
            }

            var wait = CalculateWaitTime(task);
            if (wait > TimeSpan.Zero)
            {
                var runAt = DateTime.Now.Add(wait);
                form.UpdateTaskStatus(task.Id, $"รอเวลา {runAt:yyyy-MM-dd HH:mm}");
                Console.WriteLine($"⏱️ Task '{task.Name}' จะเริ่มเมื่อ {runAt:yyyy-MM-dd HH:mm} (รอ {wait:c})");
                await Task.Delay(wait);
            }

            form.UpdateTaskStatus(task.Id, "กำลังทำงาน...");
            Console.WriteLine($"▶️ เริ่ม Task '{task.Name}'");
            try
            {
                await ExecuteTaskAsync(settings, task);
                form.UpdateTaskStatus(task.Id, "สำเร็จ ✅");
                Console.WriteLine($"✅ เสร็จ Task '{task.Name}'");
            }
            catch (LoginFailedException ex)
            {
                form.UpdateTaskStatus(task.Id, $"ล็อกอินล้มเหลว: {ex.Message}", true);
                Console.WriteLine($"❌ Login failed for Task '{task.Name}': {ex.Message}");
            }
            catch (Exception ex)
            {
                form.UpdateTaskStatus(task.Id, $"ผิดพลาด: {ex.Message}", true);
                Console.WriteLine($"❌ Task '{task.Name}' error: {ex}");
            }
        }
    }
    private static async Task ExecuteTaskAsync(RunnerSettings settings, ProfileTaskSettings task)
    {
        var profile = settings.Profiles.FirstOrDefault(p => string.Equals(p.Name, task.ProfileName, StringComparison.Ordinal));
        if (profile == null)
            throw new InvalidOperationException($"ไม่พบโปรไฟล์ \"{task.ProfileName}\" สำหรับ Task \"{task.Name}\"");

        var targetUrl = ResolveTargetUrl(settings, task);
        var screenshotBase = string.IsNullOrWhiteSpace(task.ScreenshotPathOverride) ? settings.ScreenshotPath : task.ScreenshotPathOverride!;
        var screenshotPath = GetScreenshotPath(screenshotBase, profile, task);

        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath) ?? ".");

        var persistentOpts = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = settings.Headless,
            Timeout = settings.Timeout,
            ViewportSize = new() { Width = 1280, Height = 800 },
            Channel = settings.Browser switch
            {
                BrowserChannels.Edge => BrowserChannels.Edge,
                _ => BrowserChannels.Chrome
            }
        };

        if (profile.Proxy.Enabled && !string.IsNullOrWhiteSpace(profile.Proxy.Server))
        {
            persistentOpts.Proxy = new Proxy
            {
                Server = profile.Proxy.Server,
                Username = string.IsNullOrWhiteSpace(profile.Proxy.Username) ? null : profile.Proxy.Username,
                Password = string.IsNullOrWhiteSpace(profile.Proxy.Password) ? null : profile.Proxy.Password
            };
        }

        using var playwright = await Playwright.CreateAsync();
        var browserType = playwright.Chromium;

        var userDataDir = ResolveUserDataDir(profile.UserDataDirName);
        await using var context = await browserType.LaunchPersistentContextAsync(userDataDir, persistentOpts);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        if (task.UseCredentials)
            await Login.EnsureAsync(context, settings, profile);
        else
            Console.WriteLine("ℹ️ ข้ามขั้นตอนล็อกอิน (ไม่ใช้ credential).");

        await page.GotoAsync(targetUrl, new() { Timeout = settings.Timeout, WaitUntil = WaitUntilState.NetworkIdle });

        var searchBox = page.Locator("input[type='search'], input[name='q']").First;
        if (await searchBox.CountAsync() > 0)
        {
            await searchBox.FillAsync("playwright c# cross-platform");
            await searchBox.PressAsync("Enter");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        await page.ScreenshotAsync(new() { Path = screenshotPath, FullPage = true });
        Console.WriteLine($"📸 บันทึกภาพ Task '{task.Name}' -> {screenshotPath}");
    }
    private static TimeSpan CalculateWaitTime(ProfileTaskSettings task) =>
        task.RunMode switch
        {
            TaskRunMode.Delay => task.Delay ?? TimeSpan.Zero,
            TaskRunMode.DailyTime when task.RunAtTime is TimeSpan time => CalculateDailyDelay(time, task.RepeatDaily),
            _ => TimeSpan.Zero
        };

    private static TimeSpan CalculateDailyDelay(TimeSpan time, bool repeatDaily)
    {
        var now = DateTime.Now;
        var runAt = now.Date + time;
        if (runAt <= now)
        {
            if (!repeatDaily)
                return TimeSpan.Zero;
            runAt = runAt.AddDays(1);
        }
        return runAt - now;
    }

    private static string ResolveTargetUrl(RunnerSettings settings, ProfileTaskSettings task)
    {
        var raw = string.IsNullOrWhiteSpace(task.TargetUrlOverride) ? settings.TargetUrl : task.TargetUrlOverride!;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var baseUri = TryGetBaseUri(settings.TargetUrl) ?? TryGetBaseUri(settings.Login.Url);
        if (baseUri == null)
            throw new InvalidOperationException("ไม่สามารถตีความ Target URL ได้ โปรดระบุ URL แบบเต็ม (http/https)");

        var relative = raw.TrimStart('/');
        return new Uri(baseUri, relative).ToString();
    }

    private static Uri? TryGetBaseUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return null;
        return new Uri(absolute.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static string ResolveUserDataDir(string profileFolderName)
    {
        string baseDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string root = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(baseDir, "Playwright")
            : Path.Combine(baseDir, ".cache", "playwright");

        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, profileFolderName);
        Directory.CreateDirectory(profile);
        return profile;
    }

    private static string GetScreenshotPath(string basePath, BrowserProfileSettings profile, ProfileTaskSettings task)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = "artifacts/screenshot.png";

        var fullPath = Path.GetFullPath(basePath);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(ext))
            ext = ".png";

        var sanitizedProfile = Regex.Replace(profile.Name, @"[^\w\-]+", "_");
        var sanitizedTask = Regex.Replace(task.Name, @"[^\w\-]+", "_");
        return Path.Combine(directory, $"{name}_{sanitizedProfile}_{sanitizedTask}{ext}");
    }
}
