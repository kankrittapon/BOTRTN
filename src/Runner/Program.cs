
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
        page.SetDefaultTimeout(Math.Max(settings.Timeout, 60000));
        page.SetDefaultNavigationTimeout(Math.Max(settings.Timeout, 60000));

        if (task.UseCredentials)
        {
            await Login.EnsureAsync(context, settings, profile);
            // Wait until login is confirmed by presence of a known post-login element
            try
            {
                var loginIndicators = new[]
                {
                    "xpath=//*[@id='mount_0_0_XH']/div/div[1]/div/div[3]/div/div/div[1]/div[1]/div/div[2]/div/div/div/div[2]/div/div[2]/div/div/div/div[1]/a/div[1]/div/svg/g/image"
                };
                var loginConfirmed = false;
                var activePage = context.Pages.LastOrDefault() ?? page;
                foreach (var sel in loginIndicators)
                {
                    try
                    {
                        var el = await activePage.WaitForSelectorAsync(sel, new() { State = WaitForSelectorState.Attached, Timeout = Math.Max(5000, (int)Math.Min(settings.Timeout, 15000)) });
                        if (el != null) { loginConfirmed = true; break; }
                    }
                    catch { }
                }
                Console.WriteLine(loginConfirmed ? "🔓 ยืนยันสถานะล็อกอินแล้ว" : "ℹ️ ไม่สามารถยืนยันสถานะล็อกอิน แต่จะไปยัง Target URL ต่อ");
                page = activePage;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ ตรวจสอบสถานะล็อกอินผิดพลาด: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("ℹ️ ข้ามขั้นตอนล็อกอิน (ไม่ใช้ credential).");
        }

        try
        {
            await page.GotoAsync(targetUrl, new() { Timeout = Math.Max(settings.Timeout, 60000), WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        catch (System.TimeoutException)
        {
            Console.WriteLine("⏱️ Navigation timeout, proceeding with available content.");
        }

        // Extra waits to ensure DOM and main containers are ready on dynamic sites like Facebook
        try { await page.WaitForFunctionAsync("() => document.readyState === 'complete'", null, new PageWaitForFunctionOptions { Timeout = Math.Max(settings.Timeout, 60000) }); } catch { }
        try { await page.WaitForSelectorAsync("div[id^='mount_']", new() { State = WaitForSelectorState.Attached, Timeout = 15000 }); } catch { }
        try { await page.WaitForSelectorAsync("div[role='main'], [data-pagelet]", new() { State = WaitForSelectorState.Attached, Timeout = 10000 }); } catch { }
        await Task.Delay(800);

        // Place caret inside the target <p> and insert text without clicking
        try
        {
            // Highly specific <p> selector and xpath provided
            string providedPcss = "div#mount_0_0_Xt > div:nth-of-type(1) > div:nth-of-type(1) > div.x9f619.x1n2onr6.x1ja2u2z:nth-of-type(1) > div.__fb-dark-mode.x1n2onr6.x1vjfegm:nth-of-type(5) > div.x9f619.x1n2onr6.x1ja2u2z:nth-of-type(1) > div.x78zum5.xdt5ytf.xg6iff7.xippug5.x1n2onr6:nth-of-type(1) > div.x78zum5.xdt5ytf.x1iyjqo2:nth-of-type(2) > div.x1uvtmcs.x4k7w5x.x1h91t0o.x1beo9mf.xaigb6o.x12ejxvf.x3igimt.xarpa2k.xedcshv.x1lytzrv.x1t2pt76.x7ja8zs.x1n2onr6.x1qrby5j.x1jfb8zj:nth-of-type(1) > div.__fb-dark-mode.x1qjc9v5.x9f619.x78zum5.xdt5ytf.x1iyjqo2.xl56j7k.xshlqvt:nth-of-type(1) > div.x9f619.x78zum5.xl56j7k.x2lwn1j.xeuugli.x47corl.x1qjc9v5.x1bwycvy.x135b78x.x11lfxj5.x1miatn0.x1gan7if.x2z19jh.x2tomnu:nth-of-type(1) > div.x1n2onr6.x1ja2u2z.x1afcbsf.xdt5ytf.x1a2a7pz.x71s49j.x1qjc9v5.xazwl86.x1hl0hii.x1aq6byr.x2k6n7x.x78zum5.x1plvlek.xryxfnj.xcatxm7.xrgej4m.xh8yej3:nth-of-type(1) > div.x1qjc9v5.x78zum5.xdt5ytf.x1n2onr6.x1al4vs7.x1jx94hy.xazwl86.x1hl0hii.x1aq6byr.x2k6n7x.x104qc98.x1gj8qfm.x1iyjqo2.x6ikm8r.x10wlt62.x1likypf.xzit4ce.x1e9k66k.x12l8kdc:nth-of-type(1) > div.x1fmog5m.xu25z0z.x140muxe.xo1y3bh.x78zum5.xdt5ytf.x1iyjqo2.x1al4vs7:nth-of-type(1) > div.html-div.xdj266r.x14z9mp.xat24cr.x1lziwak.xexx8yu.xyri2b.x18d9i69.x1c1uobl.x78zum5.xdt5ytf.x1iyjqo2.x1n2onr6.xqbnct6.xga75y6:nth-of-type(1) > div.html-div.xdj266r.x14z9mp.xat24cr.x1lziwak.xexx8yu.xyri2b.x18d9i69.x1c1uobl.x1jx94hy.x190bdop.xp3hrpj.x1ey2m1c.x13xjmei.xv7j57z.xh8yej3:nth-of-type(3) > div.x1n2onr6.x1ja2u2z.x9f619.x78zum5.xdt5ytf.x2lah0s.x193iq5w:nth-of-type(1) > div.x9f619.x1n2onr6.x1ja2u2z.x78zum5.xdt5ytf.x1iyjqo2.x2lwn1j:nth-of-type(1) > div.x9f619.x1n2onr6.x1ja2u2z.x78zum5.xdt5ytf.x2lah0s.x193iq5w.xf7dkkf.xv54qhq:nth-of-type(1) > div.x2lah0s.x96k8nx.xdvlbce.x1c1uobl.xyri2b:nth-of-type(1) > div.html-div.xdj266r.x14z9mp.xat24cr.x1lziwak.xexx8yu.xyri2b.x18d9i69.x1c1uobl.x1iyjqo2:nth-of-type(1) > div.x1n2onr6.x1ja2u2z.x9f619.x78zum5.xdt5ytf.x2lah0s.x193iq5w.xwib8y2.x1iorvi4:nth-of-type(1) > div.x9f619.x1n2onr6.x1ja2u2z.x78zum5.xdt5ytf.x1iyjqo2.x2lwn1j:nth-of-type(1) > div.x9f619.x1n2onr6.x1ja2u2z.x78zum5.xdt5ytf.x2lah0s.x193iq5w:nth-of-type(1) > div:nth-of-type(1) > div.html-div.xdj266r.x14z9mp.xat24cr.x1lziwak.xexx8yu.xyri2b.x18d9i69.x1c1uobl:nth-of-type(2) > div.html-div.x14z9mp.xexx8yu.xyri2b.x18d9i69.x1c1uobl.x1n2onr6.x9otpla.x1wsgfga.x1qfufaz:nth-of-type(1) > div.xwib8y2.xmzvs34.x1y1aw1k:nth-of-type(1) > div.x78zum5.x1q0g3np.x1a2a7pz:nth-of-type(1) > div.x1r8uery.x1iyjqo2.x6ikm8r.x10wlt62.xyri2b:nth-of-type(2) > form.x1ed109x.x1n2onr6.xmjcpbm.x1xn7y0n.x1uxb8k9.x1vmbcc8.x16xm01d.x972fbf.x10w94by.x1qhh985.x14e42zd.x78zum5.x1iyjqo2.x13a6bvl:nth-of-type(1) > div.xh8yej3:nth-of-type(1) > div.x78zum5.x13a6bvl:nth-of-type(1) > div.xi81zsa.xo1l8bm.xlyipyv.xuxw1ft.x49crj4.x1ed109x.xdl72j9.x1iyjqo2.xs83m0k.x6prxxf.x6ikm8r.x10wlt62.x1y1aw1k.xpdmqnj.xwib8y2.x1g0dm76:nth-of-type(1) > div.xb57i2i.x1q594ok.x5lxg6s.x78zum5.xdt5ytf.x6ikm8r.x1ja2u2z.x1pq812k.x1rohswg.xfk6m8.x1yqm8si.xjx87ck.xx8ngbg.xwo3gff.x1n2onr6.x1oyok0e.x1odjw0f.x1e4zzel.x3d5gib:nth-of-type(1) > div.x78zum5.xdt5ytf.x1iyjqo2.x1n2onr6:nth-of-type(1) > div.x1n2onr6:nth-of-type(1) > div.xzsf02u.x1a2a7pz.x1n2onr6.x14wi4xw.notranslate:nth-of-type(1) > p.xdj266r.x14z9mp.xat24cr.x1lziwak:nth-of-type(1)";
            string providedPxpath = "/html[1]/body[1]/div[1]/div[1]/div[1]/div[1]/div[5]/div[1]/div[1]/div[2]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[3]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[2]/div[1]/div[1]/div[1]/div[2]/form[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/div[1]/p[1]";

            var pSelectors = new List<string>
            {
                providedPcss,
                "xpath=" + providedPxpath,
                "p.xdj266r.x14z9mp.xat24cr.x1lziwak[dir='auto']",
                "div[contenteditable='true'] p",
                "div[role='textbox'] p"
            };

            bool inserted = false;
            for (int i = 0; i < 8 && !inserted; i++)
            {
                foreach (var sel in pSelectors)
                {
                    try
                    {
                        var handle = await page.WaitForSelectorAsync(sel, new() { State = WaitForSelectorState.Visible, Timeout = 3000 });
                        if (handle == null) continue;

                        var loc = page.Locator(sel).First;
                        await loc.ScrollIntoViewIfNeededAsync();
                        await Task.Delay(100);

                        // Place caret inside the <p> by building a collapsed range at end of the <p>, focus the contenteditable ancestor
                        try
                        {
                            await handle.EvaluateAsync(@"(p) => {
                                const editor = p.closest('[contenteditable=""true""]') || p.parentElement?.closest('[contenteditable=""true""]');
                                if (editor) {
                                    const range = document.createRange();
                                    range.selectNodeContents(p);
                                    range.collapse(false);
                                    const sel = window.getSelection();
                                    if (sel) {
                                        sel.removeAllRanges();
                                        sel.addRange(range);
                                    }
                                    editor.focus();
                                }
                            }");
                        }
                        catch { }

                        await Task.Delay(100);

                        // Try to insert text in multiple ways to satisfy React editor
                        try { await page.Keyboard.InsertTextAsync("ABCDEFG"); inserted = true; }
                        catch { try { await page.Keyboard.TypeAsync("ABCDEFG"); inserted = true; } catch { } }
                        if (!inserted)
                        {
                            try
                            {
                                await handle.EvaluateAsync(@"(p) => {
                                    const editor = p.closest('[contenteditable=""true""]');
                                    if (editor) {
                                        document.execCommand('insertText', false, 'ABCDEFG');
                                        editor.dispatchEvent(new InputEvent('input', { bubbles: true, cancelable: true, inputType: 'insertText', data: 'ABCDEFG' }));
                                    }
                                }");
                                inserted = true;
                            }
                            catch { }
                        }

                        if (inserted) break;
                    }
                    catch { }
                }

                if (!inserted)
                {
                    try { await page.EvaluateAsync("window.scrollBy(0, 800)"); await Task.Delay(400); } catch { }
                }
            }

            if (inserted)
            {
                Console.WriteLine("✅ ใส่เคอร์เซอร์ในแท็ก <p> แล้วพิมพ์ ABCDEFG สำเร็จ (ไม่คลิก)");
                try { System.Media.SystemSounds.Asterisk.Play(); } catch {}
            }
            else
            {
                Console.WriteLine("ℹ️ ไม่สามารถพิมพ์ลงใน <p> เป้าหมายได้ (selector ไม่ตรงหรือ DOM ยังไม่พร้อม)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ ตั้งเคอร์เซอร์และพิมพ์ข้อความผิดพลาด: {ex.Message}");
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
