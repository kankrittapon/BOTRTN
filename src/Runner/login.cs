using Microsoft.Playwright;

public class LoginOptions
{
    public string? Url { get; set; }
    public string UserSelector { get; set; } = "#email";
    public string PassSelector { get; set; } = "#pass";
    public string SubmitSelector { get; set; } = "button[type=submit]";

    // ถ้าล็อกอินสำเร็จควรเห็น element นี้ (เช่น avatar/logout/menu)
    public string? LoggedInCheckSelector { get; set; }

    // ถ้าระบบมีหน้า landing หลังล็อกอิน ใส่ selector ที่หน้าใหม่ให้รอ
    public string? AfterLoginWaitSelector { get; set; }

    // ถ้ามี 2FA ใส่ selector ของช่องกรอกรหัส
    public string? TwoFactorSelector { get; set; }

    public int WaitTimeout { get; set; } = 30000;
}

public static class Login
{
    /// <summary>
    /// ล็อกอินถ้ายังไม่ล็อกอิน (ใช้ Persistent Context จะจำ session ให้เอง)
    /// Credentials อ่านจาก UserSettings.json และยังสามารถ override ด้วย ENV ได้ถ้าต้องการ
    /// </summary>
    public static async Task EnsureAsync(
        IBrowserContext context,
        RunnerSettings settings,
        BrowserProfileSettings profile,
        string userEnv = "APP_USER",
        string passEnv = "APP_PASS",
        LoginOptions? options = null)
    {
        options ??= FromSettings(settings);
        if (string.IsNullOrWhiteSpace(options.Url))
            throw new ArgumentException("Login URL is not configured.");

        // อ่าน credentials: UserSettings.json > ENV
        var username = !string.IsNullOrWhiteSpace(profile.Credentials.User)
            ? profile.Credentials.User!.Trim()
            : Environment.GetEnvironmentVariable(userEnv);

        var password = !string.IsNullOrWhiteSpace(profile.Credentials.Pass)
            ? profile.Credentials.Pass!
            : Environment.GetEnvironmentVariable(passEnv);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new LoginFailedException(
                $"Credentials missing. ระบุไว้ในโปรไฟล์ \"{profile.Name}\" หรือกำหนด ENV {userEnv}/{passEnv}.");

        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        // ถ้าตั้ง LoggedInCheckSelector และ element นั้น "มองเห็น"
        if (!string.IsNullOrWhiteSpace(options.LoggedInCheckSelector))
        {
            try
            {
                var ok = await page.Locator(options.LoggedInCheckSelector!).First.IsVisibleAsync(new() { Timeout = 1200 });
                if (ok)
                {
                    Console.WriteLine("✅ Already logged in (detected LoggedInCheckSelector).");
                    return;
                }
            }
            catch { /* ignore: ไม่มี element นี้ในหน้าแรก */ }
        }

        Console.WriteLine("➡️  Navigating to login page…");
        await page.GotoAsync(options.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = options.WaitTimeout });

        // รอให้ช่องกรอก "มองเห็น" จริง
        await page.Locator(options.UserSelector).First.WaitForAsync(new()
        {
            Timeout = options.WaitTimeout,
            State = WaitForSelectorState.Visible
        });
        await page.Locator(options.PassSelector).First.WaitForAsync(new()
        {
            Timeout = options.WaitTimeout,
            State = WaitForSelectorState.Visible
        });

        Console.WriteLine("✍️  Filling credentials…");
        await page.FillAsync(options.UserSelector, username, new() { Timeout = options.WaitTimeout });
        await page.FillAsync(options.PassSelector, password, new() { Timeout = options.WaitTimeout });

        // เตรียม "เฝ้า" เหตุการณ์หลังคลิก
        Task? tLoggedIn = null;
        Task? tAfter = null;
        Task tIdle = page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = options.WaitTimeout });

        if (!string.IsNullOrWhiteSpace(options.LoggedInCheckSelector))
            tLoggedIn = page.Locator(options.LoggedInCheckSelector!).First.WaitForAsync(new()
            {
                Timeout = options.WaitTimeout,
                State = WaitForSelectorState.Visible
            });

        if (!string.IsNullOrWhiteSpace(options.AfterLoginWaitSelector))
            tAfter = page.Locator(options.AfterLoginWaitSelector!).First.WaitForAsync(new()
            {
                Timeout = options.WaitTimeout,
                State = WaitForSelectorState.Visible
            });

        Task? t2fa = null;
        if (!string.IsNullOrWhiteSpace(options.TwoFactorSelector))
            t2fa = page.Locator(options.TwoFactorSelector!).First.WaitForAsync(new()
            {
                Timeout = options.WaitTimeout,
                State = WaitForSelectorState.Visible
            });

        Console.WriteLine("🔐 Submitting login…");
        await page.ClickAsync(options.SubmitSelector, new() { Timeout = options.WaitTimeout });

        // แข่งกันรอ: LoggedInCheck / AfterLogin / 2FA / NetworkIdle
        var waiters = new List<Task> { tIdle };
        if (tLoggedIn != null) waiters.Add(tLoggedIn);
        if (tAfter != null) waiters.Add(tAfter);
        if (t2fa != null) waiters.Add(t2fa);

        Task winner;
        try
        {
            winner = await Task.WhenAny(waiters);
        }
        catch (Exception e)
        {
            await CaptureErrorScreenshot(page, "login_wait_error");
            throw new Exception("Login wait failed.", e);
        }

        if (t2fa != null && winner == t2fa)
        {
            Console.WriteLine("🛡️  2FA is required. Please complete it manually in the browser.");
            // รอจนผ่าน 2FA แล้วค่อยตรวจซ้ำว่าล็อกอินแล้ว
            if (tLoggedIn != null)
                await tLoggedIn; // จะ timeout เองถ้าไม่สำเร็จ
        }
        else
        {
            // ถ้าไม่ใช่ 2FA: ถือว่าผ่าน (จาก LoggedInCheck/AfterLogin/Idle)
            if (tLoggedIn != null)
            {
                // พยายามให้เห็น LoggedInCheck เพื่อคอนเฟิร์ม
                try { await tLoggedIn; } catch { /* บางเว็บไม่มี element นี้จริง ๆ */ }
            }
        }

        var loginError = await DetectKnownLoginErrorAsync(page);
        if (!string.IsNullOrWhiteSpace(loginError))
        {
            Console.WriteLine($"⚠️ พบข้อความผิดพลาดจากระบบล็อกอิน: {loginError}");
            throw new LoginFailedException(loginError);
        }

        Console.WriteLine("✅ Login ensure complete.");
    }

    private static LoginOptions FromSettings(RunnerSettings settings)
    {
        var s = settings.Login;
        return new LoginOptions
        {
            Url = s.Url,
            UserSelector = s.UserSelector,
            PassSelector = s.PassSelector,
            SubmitSelector = s.SubmitSelector,
            LoggedInCheckSelector = s.LoggedInCheckSelector,
            AfterLoginWaitSelector = s.AfterLoginWaitSelector,
            TwoFactorSelector = s.TwoFactorSelector,
            WaitTimeout = s.WaitTimeout
        };
    }

    private static async Task<string?> DetectKnownLoginErrorAsync(IPage page)
    {
        var knownErrors = new (string Selector, string Message)[]
        {
            ("div._9ay7", "The email address you entered isn't connected to an account.")
        };

        foreach (var (selector, message) in knownErrors)
        {
            var elements = page.Locator(selector);
            var count = await elements.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var element = elements.Nth(i);
                try
                {
                    if (!await element.IsVisibleAsync())
                        continue;

                    var text = (await element.InnerTextAsync()).Trim();
                    if (text.Contains(message, StringComparison.OrdinalIgnoreCase))
                        return message;
                }
                catch
                {
                    // ignore: element might disappear during read
                }
            }
        }

        return null;
    }

    private static async Task CaptureErrorScreenshot(IPage page, string prefix)
    {
        try
        {
            Directory.CreateDirectory("artifacts");
            var name = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            await page.ScreenshotAsync(new() { Path = Path.Combine("artifacts", name), FullPage = true });
            Console.WriteLine($"🖼️  Saved error screenshot: artifacts/{name}");
        }
        catch { /* ignore */ }
    }
}
