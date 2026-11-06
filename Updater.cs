using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows;

namespace LiteMixer;

public sealed record UpdateRelease(Version Version, string Notes, string AssetUrl, string? Sha256);

/// <summary>
/// Checks GitHub Releases for a newer version, then downloads and verifies the installer. It only ever talks to
/// GitHub, sends nothing about the user or their audio, and installs only after the user agrees.
/// </summary>
public sealed class Updater : ObservableObject
{
    public const string Repository = "vantageenterprises/litemixer";
    const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    UpdateRelease? _available;
    bool _busy;
    int _progress;
    string _status = "";
    bool _failed;

    public static Version Current
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    public static string CurrentText => Display(Current);
    public static string Display(Version version) => version.Build > 0 ? version.ToString(3) : version.ToString(2);
    static string DownloadFolder => Path.Combine(Path.GetTempPath(), "LiteMixerUpdate");

    public UpdateRelease? Available
    {
        get => _available;
        private set
        {
            if (!Set(ref _available, value)) return;
            RefreshBanner();
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string BannerText =>
        _busy ? $"Downloading update {_progress}%"
        : _failed ? "Update failed. Try again"
        : _available != null ? $"Update to {Display(_available.Version)}"
        : "";
    public Visibility BannerVisibility => _available != null ? Visibility.Visible : Visibility.Collapsed;
    public bool IsBusy => _busy;

    void RefreshBanner()
    {
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(BannerVisibility));
    }

    /// <summary>Removes the downloaded installer left over from the last update.</summary>
    public static void CleanUp()
    {
        try { if (Directory.Exists(DownloadFolder)) Directory.Delete(DownloadFolder, true); } catch { }
    }

    /// <summary>Returns true when a newer release exists. Offline or GitHub trouble only sets Status.</summary>
    public async Task<bool> CheckAsync()
    {
        if (_busy) return _available != null;
        Status = "Checking...";
        try
        {
            using var client = NewClient();
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Status = $"You're up to date ({CurrentText}).";
                return false;
            }
            response.EnsureSuccessStatusCode();
            var release = ParseRelease(await response.Content.ReadAsStringAsync());
            if (release == null || !IsNewer(release.Version, Current))
            {
                Available = null;
                Status = $"You're up to date ({CurrentText}).";
                return false;
            }
            Available = release;
            Status = $"Version {Display(release.Version)} is available.";
            return true;
        }
        catch
        {
            Status = "Couldn't reach GitHub. Try again later.";
            return false;
        }
    }

    /// <summary>Downloads and verifies the installer, returning its path. Throws with a readable reason on any failure.</summary>
    public async Task<string> DownloadVerifiedAsync(UpdateRelease release)
    {
        _busy = true;
        _failed = false;
        _progress = 0;
        RefreshBanner();
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            CleanUp();
            Directory.CreateDirectory(DownloadFolder);
            var path = Path.Combine(DownloadFolder, $"LiteMixer_{Display(release.Version)}.exe");

            using var client = NewClient();
            using var response = await client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var file = File.Create(path))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0 && (int)(done * 100 / total) != _progress)
                    {
                        _progress = (int)(done * 100 / total);
                        OnPropertyChanged(nameof(BannerText));
                    }
                }
            }

            if (release.Sha256 != null)
            {
                await using var check = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download was damaged.");
            }

            var problem = SignatureCheck.Problem(path, release.Version);
            if (problem != null) throw new InvalidDataException(problem);
            return path;
        }
        catch
        {
            CleanUp();
            _failed = true;
            throw;
        }
        finally
        {
            _busy = false;
            RefreshBanner();
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public void ReportFailure(string reason) => Status = $"Update failed: {reason}";

    public static UpdateRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName) return null;
        if (!Version.TryParse(tagName.TrimStart('v', 'V'), out var parsed)) return null;
        var version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        if (!root.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.StartsWith("LiteMixer_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            // Never download from anywhere but this repository's own releases.
            if (!url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } value &&
                value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = value["sha256:".Length..];
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateRelease(version, notes.Trim(), url, sha);
        }
        return null;
    }

    public static bool IsNewer(Version candidate, Version current) => candidate > current;

    static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiteMixer-Updater");
        return client;
    }
}

/// <summary>Decides whether a downloaded installer may run: valid signature, our publisher, the expected version.</summary>
internal static class SignatureCheck
{
    public const string Publisher = "Vantage Enterprises, LLC";
    const string CommonNameOid = "2.5.4.3";
    const string OrganizationOid = "2.5.4.10";

    /// <summary>Why the file must not run, or null when it passes every check.</summary>
    public static string? Problem(string path, Version expected)
    {
        if (!IsTrustedByWindows(path)) return "The download's signature is not valid.";

        try
        {
#pragma warning disable SYSLIB0057 // The replacement loader does not read Authenticode signatures from a file.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            string? name = null, organization = null;
            foreach (var part in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                var oid = part.GetSingleElementType().Value;
                if (oid == CommonNameOid) name = part.GetSingleElementValue();
                else if (oid == OrganizationOid) organization = part.GetSingleElementValue();
            }
            if (name != Publisher || organization != Publisher) return $"The download was not signed by {Publisher}.";
        }
        catch (CryptographicException)
        {
            return "The download's signature could not be read.";
        }

        // A validly signed old build must not be able to pose as a newer one.
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart != expected.Major || info.FileMinorPart != expected.Minor || info.FileBuildPart != Math.Max(expected.Build, 0))
            return "The download's version doesn't match the release.";
        return null;
    }

    static unsafe bool IsTrustedByWindows(string path)
    {
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        fixed (char* file = path)
        {
            var fileInfo = new WinTrustFileInfo { Size = (uint)sizeof(WinTrustFileInfo), FilePath = file };
            var data = new WinTrustData
            {
                Size = (uint)sizeof(WinTrustData),
                UiChoice = 2,          // WTD_UI_NONE
                RevocationChecks = 1,  // WTD_REVOKE_WHOLECHAIN
                UnionChoice = 1,       // WTD_CHOICE_FILE
                File = &fileInfo,
            };
            return WinVerifyTrust(-1, ref action, ref data) == 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct WinTrustFileInfo
    {
        public uint Size;
        public char* FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallback;
        public IntPtr SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public WinTrustFileInfo* File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll")]
    static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);
}
