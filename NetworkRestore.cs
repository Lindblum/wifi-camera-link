namespace WiFiCameraLink;

/// <summary>
/// Remembers which network the Wi-Fi adapter was on before joining the camera, on disk,
/// so it can be put back on close, after a crash (on next launch), or from the Disconnect button.
/// </summary>
static class NetworkRestore
{
    static readonly string StateFile = Path.Combine(AppPaths.AppDir, "restore-network.txt");

    public static bool IsPending => File.Exists(StateFile);

    public static void Remember(Guid adapterId, string? homeProfile, string cameraSsid)
    {
        // When hopping from one camera to another, keep the original home network.
        if (Read() is { } existing && existing.AdapterId == adapterId)
            homeProfile = existing.HomeProfile;
        Directory.CreateDirectory(AppPaths.AppDir);
        File.WriteAllLines(StateFile, [adapterId.ToString(), homeProfile ?? "", cameraSsid]);
    }

    /// <summary>Puts the adapter back on the home network. Returns a description of what it did, or null.</summary>
    public static string? Restore(WlanClient wlan)
    {
        var state = Read();
        if (state == null)
            return null;
        var (adapterId, home, camera) = state.Value;
        File.Delete(StateFile);

        string? current = wlan.GetCurrentProfile(adapterId);
        if (current != null && current != camera)
            return null; // already on some other network; leave it alone

        if (home.Length > 0)
        {
            wlan.ConnectProfile(adapterId, home);
            return $"Reconnecting Wi-Fi to {home}.";
        }
        if (current != null)
        {
            // No previous network recorded: drop the camera so Windows auto-joins its usual network.
            wlan.Disconnect(adapterId);
            return "Disconnected Wi-Fi from the camera.";
        }
        return null;
    }

    /// <summary>Best-effort restore with its own Wi-Fi handle, for use at process exit.</summary>
    public static void TryRestore()
    {
        if (!IsPending)
            return;
        try
        {
            using var wlan = new WlanClient();
            Restore(wlan);
        }
        catch
        {
            // Nothing more we can do while exiting; the next launch retries.
        }
    }

    static (Guid AdapterId, string HomeProfile, string CameraSsid)? Read()
    {
        try
        {
            var lines = File.ReadAllLines(StateFile);
            if (lines.Length >= 3 && Guid.TryParse(lines[0], out var id))
                return (id, lines[1], lines[2]);
        }
        catch (IOException) { }
        return null;
    }
}
