using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

// ===== โหลด config =====
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional:false, reloadOnChange:false)
    .AddJsonFile("appsettings.Development.json", optional:true, reloadOnChange:false)
    .AddEnvironmentVariables()
    .Build();

string targetUrl        = config["TargetUrl"] ?? "https://example.org/";
bool headless           = bool.TryParse(config["Headless"], out var h) ? h : true;
string browserName      = config["Browser"] ?? "chromium";
bool useInstalledChrome = bool.TryParse(config["UseInstalledChromeChannel"], out var ch) && ch;
string profileFolder    = config["UserDataDirName"] ?? "BOTRTNProfile";
int timeout             = int.TryParse(config["Timeout"], out var to) ? to : 30000;
string screenshotPath   = config["ScreenshotPath"] ?? "artifacts/screenshot.png";

Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath) ?? ".");

// Proxy (ถ้าใช้)
bool proxyEnabled = bool.TryParse(config["Proxy:Enabled"], out var pe) && pe;
string? proxyServer   = config["Proxy:Server"];
string? proxyUsername = config["Proxy:Username"];
string? proxyPassword = config["Proxy:Password"];

// Persistent Context options
var persistentOpts = new BrowserTypeLaunchPersistentContextOptions
{
    Headless = headless,
    Timeout  = timeout,
    ViewportSize = new() { Width = 1280, Height = 800 },
};

if (useInstalledChrome && browserName.Equals("chromium", StringComparison.OrdinalIgnoreCase))
    persistentOpts.Channel = "chrome";

if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyServer))
{
    persistentOpts.Proxy = new Proxy
    {
        Server   = proxyServer,
        Username = string.IsNullOrWhiteSpace(proxyUsername) ? null : proxyUsername,
        Password = string.IsNullOrWhiteSpace(proxyPassword) ? null : proxyPassword
    };
}

try
{
    using var playwright = await Playwright.CreateAsync();
    IBrowserType browserType = browserName.ToLower() switch
    {
        "firefox" => playwright.Firefox,
        "webkit"  => playwright.Webkit,
        _         => playwright.Chromium
    };

    var userDataDir = ResolveUserDataDir(profileFolder);
    await using var context = await browserType.LaunchPersistentContextAsync(userDataDir, persistentOpts);
    var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

    // 🔐 เรียกล็อกอิน (จะข้ามถ้าล็อกอินอยู่แล้ว)
    await Login.EnsureAsync(context, config);

    // ไปหน้าเป้าหมาย
    await page.GotoAsync(targetUrl, new() { Timeout = timeout, WaitUntil = WaitUntilState.NetworkIdle });

    // ถ้ามีช่องค้นหา ลองใส่สักคำ
    var searchBox = page.Locator("input[type='search'], input[name='q']").First;
    if (await searchBox.CountAsync() > 0)
    {
        await searchBox.FillAsync("playwright c# cross-platform");
        await searchBox.PressAsync("Enter");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    await page.ScreenshotAsync(new() { Path = screenshotPath, FullPage = true });
    Console.WriteLine($"✅ Screenshot -> {screenshotPath}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ {ex}");
    Environment.ExitCode = 1;
}

static string ResolveUserDataDir(string profileFolderName)
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
