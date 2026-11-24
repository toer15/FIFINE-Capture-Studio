using NAudio.CoreAudioApi;

namespace FifineCaptureStudio.Audio;

public enum AudioDeviceKind
{
    /// <summary>A microphone / line input endpoint (WASAPI capture).</summary>
    CaptureInput,

    /// <summary>A playback endpoint, captured through WASAPI loopback.</summary>
    SystemLoopback,

    /// <summary>A playback endpoint used as a monitoring output.</summary>
    PlaybackOutput
}

public sealed record AudioDeviceOption(string Id, string Name, AudioDeviceKind Kind)
{
    public bool IsLoopback => Kind == AudioDeviceKind.SystemLoopback;
    public string KindLabel => Kind switch
    {
        AudioDeviceKind.SystemLoopback => "System audio (loopback)",
        AudioDeviceKind.PlaybackOutput => "Playback device",
        _ => "Input device"
    };
    public override string ToString() => Name;
}

/// <summary>Enumerates the real Windows audio endpoints through WASAPI.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceOption> CaptureInputs()
    {
        var list = new List<AudioDeviceOption>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceOption(device.ID, device.FriendlyName, AudioDeviceKind.CaptureInput));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Core.Log.Error("Audio input enumeration failed", ex);
        }

        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<AudioDeviceOption> LoopbackDevices()
    {
        var list = new List<AudioDeviceOption>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceOption(device.ID, device.FriendlyName, AudioDeviceKind.SystemLoopback));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Core.Log.Error("Audio output enumeration failed", ex);
        }

        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? DefaultLoopbackId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                return null;
            }

            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    public static string? DefaultCaptureId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                return null;
            }

            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    public static string FriendlyName(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "System default";
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            return device.FriendlyName;
        }
        catch
        {
            return "Unavailable device";
        }
    }

    public static string FormatLabel(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unknown";
        }

        // WASAPI friendly names are long ("Microphones (USB Audio Device)"); trim at the driver name.
        return name.Length > 46 ? name[..46] + "…" : name;
    }
}
