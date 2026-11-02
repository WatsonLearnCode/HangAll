using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HangAll.Core;

/// <summary>The newest release GitHub knows about.</summary>
public sealed record UpdateInfo(
    string Version,
    string Tag,
    string PageUrl,
    string? Notes,
    string? DownloadUrl)
{
    public string Title => $"v{Version}";
}

/// <summary>
/// Asks the GitHub Releases API whether a newer build exists. It only ever reads
/// a public API and opens a page in the browser: the update itself stays a
/// download the user starts on purpose, so nothing here can run by itself.
/// </summary>
public static class Updater
{
    public enum Status { Idle, Checking, UpToDate, Available, Failed }

    /// <summary>Why a check failed, in terms the settings row can translate.</summary>
    public enum Reason { None, NoRepo, NoRelease, Timeout, Http, Network }

    private const string Repository =
        "https://api.github.com/repos/WatsonLearnCode/HangAll";

    private const string LatestRelease = Repository + "/releases/latest";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly HttpClient Http = new() { Timeout = Timeout };

    /// <summary>Set when the check starts, so a second window does not ask twice.</summary>
    private static bool _running;

    public static Status State { get; private set; } = Status.Idle;

    /// <summary>The release found by the last check, if it is newer than this build.</summary>
    public static UpdateInfo? Found { get; private set; }

    /// <summary>Why the last check failed, for the settings row.</summary>
    public static string? Failure { get; private set; }

    /// <summary>The same failure, as a kind the UI can translate.</summary>
    public static Reason FailureReason { get; private set; }

    /// <summary>Raised on the calling thread when a check finishes; the UI re-reads the state.</summary>
    public static event Action? Changed;

    /// <summary>The version this process reports, as three parts.</summary>
    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    /// <summary>True when a background check is due: the switch is on and the last one is a day old.</summary>
    public static bool ShouldCheckInBackground()
    {
        if (!Settings.Current.AutoCheckUpdates) return false;
        if (string.IsNullOrEmpty(Settings.Current.LastUpdateCheck)) return true;
        return !DateTime.TryParse(Settings.Current.LastUpdateCheck, out var last) ||
               DateTime.UtcNow - last > Daily;
    }

    /// <summary>When the last check finished, in local time, for the settings row.</summary>
    public static DateTime? LastChecked =>
        DateTime.TryParse(Settings.Current.LastUpdateCheck, out var last) ? last.ToLocalTime() : null;

    /// <summary>Asks the API once. Safe to await from the UI thread: no dialog, no throw.</summary>
    public static async Task CheckAsync()
    {
        if (_running) return;
        _running = true;
        State = Status.Checking;
        Failure = null;
        FailureReason = Reason.None;
        Changed?.Invoke();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            // GitHub asks for a User-Agent and rejects calls without one.
            request.Headers.UserAgent.ParseAdd($"HangAll/{CurrentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Failure = $"HTTP {(int)response.StatusCode}";
                // 404 is ambiguous: the repository may not be public, or it may
                // simply have no published release yet. One more call tells them
                // apart, and the two sentences are quite different.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    bool repoExists = await RepositoryExistsAsync().ConfigureAwait(false);
                    FailureReason = repoExists ? Reason.NoRelease : Reason.NoRepo;
                }
                else
                {
                    FailureReason = Reason.Http;
                }
                State = Status.Failed;
                Log.Notice($"Update check failed: {Failure} ({FailureReason})");
                return;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var info = Parse(json);
            if (info is null)
            {
                Failure = "unreadable release";
                FailureReason = Reason.Http;
                State = Status.Failed;
                Log.Notice($"Update check failed: {Failure}");
                return;
            }

            Found = IsNewer(info.Version, CurrentVersion) &&
                    !string.Equals(Settings.Current.SkipVersion, info.Version, StringComparison.OrdinalIgnoreCase)
                ? info
                : null;
            State = Found is null ? Status.UpToDate : Status.Available;
            Settings.Current.LastUpdateCheck = DateTime.UtcNow.ToString("o");
            Settings.Current.Save();
            Log.Notice($"Update check: local {CurrentVersion}, latest {info.Version}, {State}");
        }
        catch (Exception e)
        {
            bool timedOut = e is TaskCanceledException;
            Failure = timedOut ? "timed out" : e.Message;
            FailureReason = timedOut ? Reason.Timeout : Reason.Network;
            State = Status.Failed;
            Log.Notice($"Update check failed: {Failure}");
        }
        finally
        {
            _running = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Whether the repository itself is readable, which separates "no release" from "not public".</summary>
    private static async Task<bool> RepositoryExistsAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Repository);
            request.Headers.UserAgent.ParseAdd($"HangAll/{CurrentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The release page, opened in the default browser.</summary>
    public static void OpenPage()
    {
        if (Found is { PageUrl.Length: > 0 } info) Interop.Shell.OpenUrl(info.PageUrl);
    }

    /// <summary>Stops the reminders for one release without hiding later ones.</summary>
    public static void Skip()
    {
        if (Found is not { } info) return;
        Settings.Current.SkipVersion = info.Version;
        Settings.Current.Save();
        Found = null;
        State = Status.UpToDate;
        Changed?.Invoke();
    }

    /// <summary>Reads the release body into the fields the settings row needs.</summary>
    private static UpdateInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            string page = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() ?? "" : "";
            string? notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;

            string? download = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    if (!name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) continue;
                    download = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    break;
                }
            }

            string version = NormaliseVersion(tag);
            return version.Length == 0 ? null : new UpdateInfo(version, tag, page, notes, download);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turns "v1.2.3", "1.2.3" or "v1.2.3-beta" into "1.2.3".</summary>
    internal static string NormaliseVersion(string tag)
    {
        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) text = text[..dash];
        return System.Version.TryParse(text, out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}"
            : "";
    }

    /// <summary>Compares two three-part versions; a missing part counts as zero.</summary>
    internal static bool IsNewer(string candidate, string current)
    {
        if (!System.Version.TryParse(candidate, out var there)) return false;
        if (!System.Version.TryParse(current, out var here)) return false;
        return Normalise(there) > Normalise(here);

        static System.Version Normalise(System.Version value) =>
            new(value.Major, value.Minor, Math.Max(0, value.Build));
    }
}
