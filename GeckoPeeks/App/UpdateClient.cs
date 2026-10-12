using System.Net.Http.Headers;

namespace GeckoPeeks.App;

/// <summary>
/// Talks to the GitHub Releases API and downloads GeckoPeeks.exe. Failures are returned as
/// null / thrown messages the tray turns into a short MessageBox — never a crash.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateClient(HttpClient? http = null)
    {
        if (http != null)
        {
            _http = http;
            _ownsClient = false;
            return;
        }

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsClient = true;
        // GitHub requires a User-Agent; the version string helps when reading their logs.
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("GeckoPeeks", AppUpdate.FormatVersion(AppUpdate.CurrentVersion())));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Null when this build is already current, or the feed cannot be read.</summary>
    public async Task<UpdateOffer?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(AppUpdate.ApiLatestUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return AppUpdate.PickOffer(json, current);
    }

    /// <summary>
    /// Writes the asset next to the running exe as <c>GeckoPeeks.exe.new</c>. Checks the MZ
    /// header and a minimum size so a truncated HTML error page is never installed.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, string exePath, CancellationToken cancellationToken = default)
    {
        var destination = exePath + ".new";
        var temp = destination + ".partial";
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);

            using (var response = await _http.GetAsync(offer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(temp);
            if (info.Length < 1_000_000)
                throw new InvalidOperationException("The download was too small to be GeckoPeeks.exe.");
            if (offer.SizeBytes > 0 && Math.Abs(info.Length - offer.SizeBytes) > 1024)
                throw new InvalidOperationException("The download size did not match the release asset.");

            await using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                    throw new InvalidOperationException("The download is not a Windows executable.");
            }

            File.Move(temp, destination, overwrite: true);
            return destination;
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Best-effort cleanup.
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
