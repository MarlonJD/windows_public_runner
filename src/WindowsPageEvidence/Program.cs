using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

var config = EvidenceConfig.FromEnvironment();
Directory.CreateDirectory(config.ScreenshotDirectory);

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

using var driver = new PageEvidenceDriver(config);
var result = driver.Run();

var manifest = new EvidenceManifest(
    Status: result.Pages.All(page => page.Status == "passed") ? "passed" : "failed",
    CapturedAtUtc: DateTimeOffset.UtcNow,
    SourceRef: config.SourceRef,
    QaLabel: config.QaLabel,
    Language: config.Language,
    Theme: config.Theme,
    Backend: config.Backend,
    AppEnvironment: config.AppEnvironment,
    BaseUrlHost: SafeHost(config.BaseUrl),
    UsernameHash: Sha256(config.Username),
    Pages: result.Pages,
    Notes: [
        "Temporary public Windows runner evidence while winui3-mac-test-runtime is incomplete.",
        "Raw credentials, full URLs, account names, and local paths are intentionally omitted."
    ]);

Directory.CreateDirectory(Path.GetDirectoryName(config.ManifestPath)!);
File.WriteAllText(config.ManifestPath, JsonSerializer.Serialize(manifest, jsonOptions));

Console.WriteLine($"Manifest: {config.ManifestPath}");
Console.WriteLine($"Screenshots: {config.ScreenshotDirectory}");
Console.WriteLine($"Status: {manifest.Status}");

return manifest.Status == "passed" ? 0 : 65;


static string? SafeHost(string? url)
{
    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return uri.Host;
    }

    return null;
}

static string Sha256(string value)
{
    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

sealed record EvidenceConfig(
    string Aumid,
    string ScreenshotDirectory,
    string ManifestPath,
    string Username,
    string Password,
    string? BaseUrl,
    string Backend,
    string AppEnvironment,
    string Language,
    string Theme,
    string SourceRef,
    string QaLabel)
{
    public static EvidenceConfig FromEnvironment()
    {
        var screenshotDir = Read("EMSI_UI_SCREENSHOT_DIR") ?? Path.Combine(Path.GetTempPath(), "emsi-windows-page-evidence", "screenshots");
        var manifestPath = Read("EMSI_UI_MANIFEST_PATH") ?? Path.Combine(Path.GetDirectoryName(screenshotDir) ?? screenshotDir, "manifest.json");

        return new EvidenceConfig(
            Required("EMSI_WINDOWS_APP_AUMID"),
            screenshotDir,
            manifestPath,
            Required("EMSI_WINDOWS_USERNAME"),
            Required("EMSI_WINDOWS_PASSWORD"),
            Read("EMSI_API_BASE_URL"),
            Read("EMSI_API_BACKEND") ?? "go",
            Read("EMSI_APP_ENVIRONMENT") ?? "preprod",
            Read("EMSI_APP_LANGUAGE") ?? "tr-TR",
            Read("EMSI_APP_THEME") ?? "system",
            Read("EMSI_SOURCE_REF") ?? "main",
            Read("EMSI_QA_LABEL") ?? "public-runner");
    }

    public string AppArguments()
    {
        var args = new List<string>
        {
            "--evidence-mode",
            $"--language={Language}",
            $"--theme={Theme}",
            $"--backend={Backend}",
            $"--environment={AppEnvironment}"
        };

        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            args.Add($"--base-url={BaseUrl}");
        }

        return string.Join(' ', args.Select(Quote));
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    private static string? Read(string name) => Environment.GetEnvironmentVariable(name);
    private static string Required(string name) => Read(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Missing required environment variable: {name}");
}

sealed class PageEvidenceDriver(EvidenceConfig config) : IDisposable
{
    private static readonly PageTarget[] SignedInPages =
    [
        new("home", "shell-nav-home", "Home"),
        new("channels", "shell-nav-channels", "Channels"),
        new("events", "shell-nav-events", "Events"),
        new("messages", "shell-nav-messages", "Messages"),
        new("notifications", "shell-nav-notifications", "Notifications"),
        new("settings", "shell-nav-settings", "Settings"),
        new("admin", "shell-nav-admin", "Admin", Optional: true)
    ];

    private readonly UIA3Automation automation = new();
    private Application? app;
    private Window? window;

    public EvidenceResult Run()
    {
        var pages = new List<PageCapture>();
        Launch();
        window = WaitForMainWindow();
        pages.Add(CaptureCurrent("login", "Login", "visible"));
        SignIn();

        foreach (var page in SignedInPages)
        {
            var item = Find(page.AutomationId);
            if (item is null && page.Optional)
            {
                pages.Add(new PageCapture(page.Id, page.Title, null, "skipped", "Navigation item not visible for this account."));
                continue;
            }

            if (item is null)
            {
                pages.Add(new PageCapture(page.Id, page.Title, null, "failed", $"Missing navigation item {page.AutomationId}."));
                continue;
            }

            Invoke(item);
            Thread.Sleep(900);
            pages.Add(CaptureCurrent(page.Id, page.Title, "visible"));
        }

        return new EvidenceResult(pages);
    }

    private void Launch()
    {
        app = Application.LaunchStoreApp(config.Aumid, config.AppArguments());
    }

    private Window WaitForMainWindow()
    {
        return Retry.WhileNull(
            () => app!.GetMainWindow(automation),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMilliseconds(500)).Result
            ?? throw new InvalidOperationException("The app main window did not appear within 60s.");
    }

    private void SignIn()
    {
        var username = WaitFor("login-username", TimeSpan.FromSeconds(25));
        if (username is null)
        {
            if (Find("shell-nav-home") is not null) return;
            throw new InvalidOperationException("Login form did not appear.");
        }

        EnterText(username, config.Username);
        EnterPassword(Require("login-password"), config.Password);
        Invoke(Require("login-submit"));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(35);
        while (DateTime.UtcNow < deadline)
        {
            if (Find("shell-nav-home") is not null) return;
            var error = ReadText(Find("login-error"));
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("Sign-in failed; see private app logs.");
            Thread.Sleep(300);
        }

        throw new InvalidOperationException("Signed-in shell did not appear after login.");
    }

    private PageCapture CaptureCurrent(string id, string title, string state)
    {
        var fileName = $"windows-page-{SafeFilePart(id)}.jpg";
        var path = Path.Combine(config.ScreenshotDirectory, fileName);
        try
        {
            window!.SetForeground();
            Thread.Sleep(250);
            using var image = Capture.Element(window).Bitmap;
            var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
            image.Save(path, encoder, parameters);
            return new PageCapture(id, title, fileName, File.Exists(path) ? "passed" : "failed", null, state);
        }
        catch (Exception ex)
        {
            return new PageCapture(id, title, null, "failed", ex.GetType().Name, state);
        }
    }

    private AutomationElement? Find(string automationId) => window?.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
    private AutomationElement Require(string automationId) => Find(automationId) ?? throw new InvalidOperationException($"Required element '{automationId}' not found.");
    private AutomationElement? WaitFor(string automationId, TimeSpan timeout) => Retry.WhileNull(() => Find(automationId), timeout, TimeSpan.FromMilliseconds(250)).Result;

    private static void Invoke(AutomationElement element)
    {
        var invoke = element.Patterns.Invoke.PatternOrDefault;
        if (invoke is not null) { invoke.Invoke(); return; }
        var selection = element.Patterns.SelectionItem.PatternOrDefault;
        if (selection is not null) { selection.Select(); return; }
        element.Click();
    }

    private static void EnterText(AutomationElement element, string text)
    {
        element.Focus();
        var value = element.Patterns.Value.PatternOrDefault;
        if (value is not null && !value.IsReadOnly.ValueOrDefault)
        {
            try { value.SetValue(text); return; } catch { }
        }
        element.Click();
        Keyboard.Type(text);
    }

    private static void EnterPassword(AutomationElement element, string password)
    {
        element.Focus();
        element.Click();
        Keyboard.Type(password);
    }

    private static string? ReadText(AutomationElement? element)
    {
        if (element is null) return null;
        try { if (!string.IsNullOrWhiteSpace(element.Name)) return element.Name; } catch { }
        try { return element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault; } catch { return null; }
    }

    private static string SafeFilePart(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    public void Dispose()
    {
        try
        {
            if (app is { HasExited: false })
            {
                app.Close();
                Thread.Sleep(1000);
                if (!app.HasExited) app.Kill();
            }
        }
        catch { }
        finally
        {
            automation.Dispose();
        }
    }
}

sealed record PageTarget(string Id, string AutomationId, string Title, bool Optional = false);
sealed record EvidenceResult(IReadOnlyList<PageCapture> Pages);
sealed record PageCapture(string Id, string Title, string? ScreenshotFileName, string Status, string? Note = null, string? State = null);
sealed record EvidenceManifest(string Status, DateTimeOffset CapturedAtUtc, string SourceRef, string QaLabel, string Language, string Theme, string Backend, string AppEnvironment, string? BaseUrlHost, string UsernameHash, IReadOnlyList<PageCapture> Pages, IReadOnlyList<string> Notes);
