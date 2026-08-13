#nullable enable

using System;
using System.Globalization;
using System.IO;

namespace JukeBox.Game.Beatmaps;

public class OsuFileInfo
{
    public string? AudioFilename;
    public int Mode;
    public string? BackgroundFilename;
    public string? VideoFilename;

    /// <summary>[Metadata] Version — the difficulty name (e.g. "Easy", "Insane").</summary>
    public string? Version;
}

public class OsuFileScanner
{
    // Reads [General] (AudioFilename, Mode), [Metadata] (Version) and
    // [Events] (background "0,0,\"bg.jpg\"" and video "Video,offset,\"v.mp4\"" / "1,offset,..." lines).
    public static OsuFileInfo Scan(string osuPath)
    {
        var info = new OsuFileInfo();
        string? section = null;

        foreach (string rawLine in File.ReadLines(osuPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                if (section == "Events")
                    break; // done scanning Events, stop at the next section

                section = line.Substring(1, line.Length - 2);
                continue;
            }

            switch (section)
            {
                case "General":
                    int colon = line.IndexOf(':');
                    if (colon < 0)
                        break;

                    string key = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();

                    switch (key)
                    {
                        case "AudioFilename":
                            info.AudioFilename = value;
                            break;
                        case "Mode":
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out info.Mode);
                            break;
                    }
                    break;

                case "Metadata":
                    int metaColon = line.IndexOf(':');
                    if (metaColon < 0)
                        break;

                    if (line.Substring(0, metaColon).Trim() == "Version")
                        info.Version = line.Substring(metaColon + 1).Trim();
                    break;

                case "Events":
                    string[] parts = line.Split(',');
                    if (parts.Length < 3)
                        break;

                    string type = parts[0].Trim();
                    if (type == "0")
                    {
                        info.BackgroundFilename = stripQuotes(parts[2]);
                    }
                    else if (type.Equals("Video", StringComparison.OrdinalIgnoreCase) || type == "1")
                    {
                        info.VideoFilename = stripQuotes(parts[2]);
                    }
                    break;
            }
        }

        return info;
    }

    private static string stripQuotes(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);
        return s;
    }
}
