using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LootPulse.Services
{
    public class ZoneChangedEventArgs : EventArgs
    {
        public string ZoneName { get; }
        public int ZoneLevel { get; }

        public ZoneChangedEventArgs(string zoneName, int zoneLevel)
        {
            ZoneName = zoneName;
            ZoneLevel = zoneLevel;
        }
    }

    public class ClientLogMonitor
    {
        private string _logFilePath = string.Empty;
        private CancellationTokenSource? _cts;
        private long _lastMaxOffset;

        public event EventHandler<ZoneChangedEventArgs>? ZoneChanged;

        // Matches lines like: 2026/06/14 02:30:28 123456 78a [Info Client 1234] : You have entered Lioneye's Watch.
        private static readonly Regex ZoneRegex = new Regex(@": You have entered (.+?)\.", RegexOptions.Compiled);
        private static readonly Regex LevelRegex = new Regex(@"Generating level (\d+) area", RegexOptions.Compiled);
        private int _lastGeneratedLevel = 1;

        public void StartMonitoring(string logFilePath)
        {
            if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
            {
                System.Diagnostics.Debug.WriteLine($"Log file does not exist: {logFilePath}");
                return;
            }

            _logFilePath = logFilePath;
            _cts = new CancellationTokenSource();

            // Set initial offset to the end of the file to ignore old history
            try
            {
                var fileInfo = new FileInfo(_logFilePath);
                _lastMaxOffset = fileInfo.Length;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading log file metadata: {ex.Message}");
                _lastMaxOffset = 0;
            }

            Task.Run(() => MonitorLoopAsync(_cts.Token));
        }

        public void StopMonitoring()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task MonitorLoopAsync(CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine("Starting Client.txt log monitor loop.");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(_logFilePath))
                    {
                        var fileInfo = new FileInfo(_logFilePath);
                        long currentLength = fileInfo.Length;

                        if (currentLength < _lastMaxOffset)
                        {
                            // File was truncated or rolled over
                            _lastMaxOffset = 0;
                        }

                        if (currentLength > _lastMaxOffset)
                        {
                            // Open file with FileShare.ReadWrite to avoid locking issues
                            using (var stream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                stream.Seek(_lastMaxOffset, SeekOrigin.Begin);
                                using (var reader = new StreamReader(stream, Encoding.UTF8))
                                {
                                    string? line;
                                    while ((line = await reader.ReadLineAsync(token)) != null)
                                    {
                                        var lvlMatch = LevelRegex.Match(line);
                                        if (lvlMatch.Success)
                                        {
                                            if (int.TryParse(lvlMatch.Groups[1].Value, out int lvl))
                                            {
                                                _lastGeneratedLevel = lvl;
                                            }
                                        }

                                        var match = ZoneRegex.Match(line);
                                        if (match.Success)
                                        {
                                            var zoneName = match.Groups[1].Value.Trim();
                                            int level = _lastGeneratedLevel;
                                            if (level <= 1)
                                            {
                                                level = GetZoneLevelFromName(zoneName);
                                            }
                                            System.Diagnostics.Debug.WriteLine($"Detected zone transition: {zoneName} (Level {level})");
                                            ZoneChanged?.Invoke(this, new ZoneChangedEventArgs(zoneName, level));
                                            _lastGeneratedLevel = 1; // Reset for next zone
                                        }
                                    }
                                    _lastMaxOffset = stream.Position;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in log monitor loop: {ex.Message}");
                }

                // Poll every 500 milliseconds
                await Task.Delay(500, token);
            }

            System.Diagnostics.Debug.WriteLine("Stopped Client.txt log monitor loop.");
        }

        public static int GetZoneLevelFromName(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName)) return 1;

            var clean = zoneName.Trim().ToLowerInvariant();

            // Act 1
            if (clean.Contains("oakhaven")) return 1;
            if (clean.Contains("lioneye's watch")) return 1;
            if (clean.Contains("the coast")) return 2;
            if (clean.Contains("mud flats")) return 3;
            if (clean.Contains("the grotto")) return 4;
            if (clean.Contains("the ridge")) return 5;
            if (clean.Contains("great forest")) return 6;
            if (clean.Contains("hooded copse")) return 7;
            if (clean.Contains("riverways") || clean.Contains("the riverways")) return 8;
            if (clean.Contains("oasis") || clean.Contains("the oasis")) return 10;
            
            // Act 2
            if (clean.Contains("forest encampment")) return 16;
            if (clean.Contains("fields") || clean.Contains("the fields")) return 16;
            if (clean.Contains("ruins") || clean.Contains("the ruins")) return 18;
            
            // Act 3
            if (clean.Contains("sarn encampment")) return 33;
            if (clean.Contains("slums") || clean.Contains("the slums")) return 33;

            // Endgame / Waystone parsing or maps
            var levelInNameMatch = Regex.Match(zoneName, @"\bLevel\s+(\d+)");
            if (levelInNameMatch.Success && int.TryParse(levelInNameMatch.Groups[1].Value, out int lvl))
            {
                return lvl;
            }

            return 1;
        }
    }
}
