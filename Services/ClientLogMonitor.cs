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

    public class PlayerLevelChangedEventArgs : EventArgs
    {
        public string CharacterName { get; }
        public int Level { get; }

        public PlayerLevelChangedEventArgs(string characterName, int level)
        {
            CharacterName = characterName;
            Level = level;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The background log monitor loop must catch all exceptions to continue reading gameplay logs gracefully without crashing.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Running inside task worker threads where synchronization context is not explicitly coupled to UI updates but safe to proceed.")]
    public sealed class ClientLogMonitor : IDisposable
    {
        private string _logFilePath = string.Empty;
        private CancellationTokenSource? _cts;
        private long _lastMaxOffset;

        public event EventHandler<ZoneChangedEventArgs>? ZoneChanged;
        public event EventHandler<PlayerLevelChangedEventArgs>? PlayerLevelChanged;

        // Matches lines like: ... [SCENE] Set Source [Lioneye's Watch]
        private static readonly Regex ZoneRegex = new Regex(@"\[SCENE\] Set Source \[(.+?)\]$", RegexOptions.Compiled);
        private const string NullSceneSource = "(null)";
        // Matches lines like: ... Generating level 42 area ...
        private static readonly Regex AreaLevelRegex = new Regex(@"Generating level (\d+) area", RegexOptions.Compiled);
        // Matches lines like: ... : MyCharacter is now level 55
        private static readonly Regex CharLevelRegex = new Regex(@"] : (.+?) is now level (\d+)", RegexOptions.Compiled);
        // Identifies the *active* character, logged on every instance load:
        //   ... [WARN Client] Character Light_Fisted has unknown build of path: None
        private static readonly Regex ActiveCharRegex = new Regex(@"Character (\S+) has .* build of path", RegexOptions.Compiled);
        private int _lastGeneratedLevel = 1;
        private string? _currentCharacterName;

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

            // Scan recent history off the UI thread (large logs can require reading far back).
            Task.Run(() => ScanRecentHistory());
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
                                        // Check for area-level generation (zone level, not character level)
                                        var areaLvlMatch = AreaLevelRegex.Match(line);
                                        if (areaLvlMatch.Success)
                                        {
                                            if (int.TryParse(areaLvlMatch.Groups[1].Value, out int lvl))
                                            {
                                                _lastGeneratedLevel = lvl;
                                            }
                                        }

                                        // Track the active character (logged on each instance load).
                                        var activeMatch = ActiveCharRegex.Match(line);
                                        if (activeMatch.Success)
                                        {
                                            string activeName = activeMatch.Groups[1].Value.Trim();
                                            if (!string.Equals(activeName, _currentCharacterName, StringComparison.Ordinal))
                                            {
                                                System.Diagnostics.Debug.WriteLine($"Active character switched to: {activeName}");
                                                _currentCharacterName = activeName;
                                                // Name is known now; level will follow on the next level-up
                                                // or via a background lookup of this character's last level.
                                                int known = FindLatestLevelForCharacter(activeName);
                                                PlayerLevelChanged?.Invoke(this, new PlayerLevelChangedEventArgs(activeName, known));
                                            }
                                        }

                                        // Check for character level-up (attribute to the active character only).
                                        var charLvlMatch = CharLevelRegex.Match(line);
                                        if (charLvlMatch.Success)
                                        {
                                            string charName = charLvlMatch.Groups[1].Value.Trim();
                                            if (int.TryParse(charLvlMatch.Groups[2].Value, out int charLevel)
                                                && (_currentCharacterName == null || NamesMatch(charName, _currentCharacterName)))
                                            {
                                                _currentCharacterName = StripClass(charName);
                                                System.Diagnostics.Debug.WriteLine($"Detected level-up: {charName} is now level {charLevel}");
                                                PlayerLevelChanged?.Invoke(this, new PlayerLevelChangedEventArgs(_currentCharacterName, charLevel));
                                            }
                                        }

                                        // Check for zone transition
                                        var zoneMatch = ZoneRegex.Match(line);
                                        if (zoneMatch.Success && !string.Equals(zoneMatch.Groups[1].Value, NullSceneSource, StringComparison.Ordinal))
                                        {
                                            var zoneName = zoneMatch.Groups[1].Value.Trim();
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
                catch (OperationCanceledException)
                {
                    break;
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

        /// <summary>
        /// Reads the tail of the log file to find the most recent character level-up
        /// and zone entry, then fires events so the UI starts with accurate data.
        /// </summary>
        private void ScanRecentHistory()
        {
            // Determine the *active* character from the most recent instance-load line (this is the
            // character currently being played, not merely whichever one leveled up most recently),
            // then look up that character's last known level. Both can be far back in a large log.
            try
            {
                var nameMatch = FindLatestLineMatch(ActiveCharRegex);
                string? activeName = nameMatch?.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(activeName))
                {
                    _currentCharacterName = activeName;
                    int level = FindLatestLevelForCharacter(activeName);
                    System.Diagnostics.Debug.WriteLine($"Restored active character from log: {activeName} level {level}");
                    PlayerLevelChanged?.Invoke(this, new PlayerLevelChangedEventArgs(activeName, level));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning character history: {ex.Message}");
            }

            // The current zone appears frequently, so the recent tail is enough.
            try
            {
                const int tailBytes = 128 * 1024; // Read last 128KB
                using var stream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                long startPos = Math.Max(0, stream.Length - tailBytes);
                stream.Seek(startPos, SeekOrigin.Begin);

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? lastZoneName = null;
                int lastZoneLevel = 1;
                int lastAreaLevel = 1;

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var areaMatch = AreaLevelRegex.Match(line);
                    if (areaMatch.Success && int.TryParse(areaMatch.Groups[1].Value, out int aLvl))
                    {
                        lastAreaLevel = aLvl;
                    }

                    var zoneMatch = ZoneRegex.Match(line);
                    if (zoneMatch.Success && !string.Equals(zoneMatch.Groups[1].Value, NullSceneSource, StringComparison.Ordinal))
                    {
                        lastZoneName = zoneMatch.Groups[1].Value.Trim();
                        lastZoneLevel = lastAreaLevel > 1 ? lastAreaLevel : GetZoneLevelFromName(lastZoneName);
                        lastAreaLevel = 1; // Reset for next zone
                    }
                }

                if (!string.IsNullOrEmpty(lastZoneName))
                {
                    System.Diagnostics.Debug.WriteLine($"Restored from log: zone {lastZoneName} (level {lastZoneLevel})");
                    ZoneChanged?.Invoke(this, new ZoneChangedEventArgs(lastZoneName, lastZoneLevel));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning recent zone history: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the most recent level reached by a specific character, or 0 if not found in the log.
        /// </summary>
        private int FindLatestLevelForCharacter(string characterName)
        {
            string baseName = StripClass(characterName);
            // e.g. "Light_Fisted (Martial Artist) is now level 94" — class part is optional.
            var regex = new Regex(Regex.Escape(baseName) + @"(?: \([^)]*\))? is now level (\d+)", RegexOptions.Compiled);
            var match = FindLatestLineMatch(regex);
            if (match != null && int.TryParse(match.Groups[1].Value, out int level))
            {
                return level;
            }
            return 0;
        }

        /// <summary>
        /// Searches the log backwards in chunks and returns the most recent line matching the
        /// given regex (or null). Stops as soon as a match is found, so it is cheap when the
        /// target line is recent even in a multi-hundred-MB log.
        /// </summary>
        private Match? FindLatestLineMatch(Regex regex)
        {
            using var stream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            const int chunkSize = 256 * 1024;
            long pos = stream.Length;
            var buffer = new byte[chunkSize];
            string carry = string.Empty; // partial line carried from the previous (later) chunk

            while (pos > 0)
            {
                int toRead = (int)Math.Min(chunkSize, pos);
                pos -= toRead;
                stream.Seek(pos, SeekOrigin.Begin);

                int read = 0;
                while (read < toRead)
                {
                    int n = stream.Read(buffer, read, toRead - read);
                    if (n == 0) break;
                    read += n;
                }

                // This chunk is earlier in the file, so the carried partial line goes on the end.
                string text = Encoding.UTF8.GetString(buffer, 0, read) + carry;

                var matches = regex.Matches(text);
                if (matches.Count > 0)
                {
                    return matches[matches.Count - 1]; // last match = most recent in this chunk
                }

                // Carry this chunk's incomplete first line to the next (earlier) chunk.
                int firstNl = text.IndexOf('\n', StringComparison.Ordinal);
                carry = firstNl >= 0 ? text.Substring(0, firstNl) : text;
            }

            return null;
        }

        // "Light_Fisted (Martial Artist)" -> "Light_Fisted"
        private static string StripClass(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name.Substring(0, paren) : name;
        }

        private static bool NamesMatch(string a, string b)
            => string.Equals(StripClass(a), StripClass(b), StringComparison.Ordinal);

        public static int GetZoneLevelFromName(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName)) return 1;

            // Act 1
            if (zoneName.Contains("oakhaven", StringComparison.OrdinalIgnoreCase)) return 1;
            if (zoneName.Contains("lioneye's watch", StringComparison.OrdinalIgnoreCase)) return 1;
            if (zoneName.Contains("the coast", StringComparison.OrdinalIgnoreCase)) return 2;
            if (zoneName.Contains("mud flats", StringComparison.OrdinalIgnoreCase)) return 3;
            if (zoneName.Contains("the grotto", StringComparison.OrdinalIgnoreCase)) return 4;
            if (zoneName.Contains("the ridge", StringComparison.OrdinalIgnoreCase)) return 5;
            if (zoneName.Contains("great forest", StringComparison.OrdinalIgnoreCase)) return 6;
            if (zoneName.Contains("hooded copse", StringComparison.OrdinalIgnoreCase)) return 7;
            if (zoneName.Contains("riverways", StringComparison.OrdinalIgnoreCase)) return 8;
            if (zoneName.Contains("oasis", StringComparison.OrdinalIgnoreCase)) return 10;

            // Act 2
            if (zoneName.Contains("forest encampment", StringComparison.OrdinalIgnoreCase)) return 16;
            if (zoneName.Contains("fields", StringComparison.OrdinalIgnoreCase)) return 16;
            if (zoneName.Contains("ruins", StringComparison.OrdinalIgnoreCase)) return 18;

            // Act 3
            if (zoneName.Contains("sarn encampment", StringComparison.OrdinalIgnoreCase)) return 33;
            if (zoneName.Contains("slums", StringComparison.OrdinalIgnoreCase)) return 33;

            // Endgame / Waystone parsing or maps
            var levelInNameMatch = Regex.Match(zoneName, @"\bLevel\s+(\d+)");
            if (levelInNameMatch.Success && int.TryParse(levelInNameMatch.Groups[1].Value, out int lvl))
            {
                return lvl;
            }

            return 1;
        }

        public void Dispose()
        {
            StopMonitoring();
            GC.SuppressFinalize(this);
        }
    }
}
