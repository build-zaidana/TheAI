using global::Windows.Devices.Enumeration;

namespace CodexBar.App.Hud;

/// <summary>
/// The battery level Windows knows for a Bluetooth audio device (the same number Settings shows
/// under Bluetooth &amp; devices), found by matching the audio output's name to the paired device.
/// Many earphones never report it; then there is simply no number.
/// </summary>
internal static class BluetoothBattery
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string BluetoothClass = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";

    /// <summary>0–100, or null when the device doesn't say.</summary>
    public static async Task<int?> ForOutputAsync(string outputName)
    {
        if (outputName.Length == 0)
        {
            return null;
        }

        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                $"System.Devices.ClassGuid:=\"{BluetoothClass}\"", [BatteryKey], DeviceInformationKind.Device);
            // "Headphones (Adrian's Buds3)" or "Adrian's Buds3 Stereo" ↔ the paired "Adrian's Buds3".
            var best = devices
                .Where(device => device.Name.Length > 2 && outputName.Contains(device.Name, StringComparison.OrdinalIgnoreCase))
                .Select(device => device.Properties.TryGetValue(BatteryKey, out var value) && value is byte level ? (int?)level : null)
                .Where(level => level is >= 0 and <= 100)
                .ToList();
            return best.Count > 0 ? best.Min() : null;   // two earbuds: the lower one is what matters
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Bluetooth", exception.Message);
            return null;
        }
    }
}
