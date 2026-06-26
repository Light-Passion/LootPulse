using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LootPulse.Services
{
    public class ZoneChangedEventArgs(string zoneName, int zoneLevel) : EventArgs
    {
        public string ZoneName { get; } = zoneName;
        public int ZoneLevel { get; } = zoneLevel;
    }

    public class PlayerLevelChangedEventArgs(string characterName, int level) : EventArgs
    {
        public string CharacterName { get; } = characterName;
        public int Level { get; } = level;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The background log monitor loop must catch all exceptions to continue reading gameplay logs gracefully without crashing.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Running inside task worker threads where synchronization context is not explicitly coupled to UI updates but safe to proceed.")]
    public sealed partial class ClientLogMonitor : IDisposable
    {
        private string _logFilePath = string.Empty;
        private CancellationTokenSource? _cts;
        private long _lastMaxOffset;

        public Func<string[]?>? ActiveBuildClassSynonymsProvider { get; set; }

        public event EventHandler<ZoneChangedEventArgs>? ZoneChanged;
        public event EventHandler<PlayerLevelChangedEventArgs>? PlayerLevelChanged;

        [GeneratedRegex(@"\[SCENE\] Set Source \[(.+?)\]$")]
        private static partial Regex ZoneRegex();

        [GeneratedRegex(@"Generating level (\d+) area")]
        private static partial Regex AreaLevelRegex();

        [GeneratedRegex(@"] : (.+?) is now level (\d+)")]
        private static partial Regex CharLevelRegex();

        [GeneratedRegex(@"Character (\S+) has .* build of path")]
        private static partial Regex ActiveCharRegex();

        [GeneratedRegex(@"] : (\S+) \(([^)]+)\) is now level (\d+)")]
        private static partial Regex AnyLevelRegex();

        [GeneratedRegex(@"\bLevel\s+(\d+)")]
        private static partial Regex LevelInNameRegex();

        private const string _nullSceneSource = "(null)";
        private int _lastGeneratedLevel = 1;
        private string? _currentCharacterName;
        private bool _pendingLoginScreen;

        private string? _cachedSynonymsKey;
        private Regex? _cachedClassLevelRegex;

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

        public void TriggerHistoryScan()
        {
            Task.Run(() => ScanRecentHistory());
        }

        private async Task MonitorLoopAsync(CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine("Starting Client.txt log monitor loop.");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await CheckAndReadLogFileAsync(token);
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

        private async Task CheckAndReadLogFileAsync(CancellationToken token)
        {
            if (!File.Exists(_logFilePath))
            {
                return;
            }

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
                await using var stream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(_lastMaxOffset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(token)) != null)
                {
                    ProcessLogLine(line);
                }
                _lastMaxOffset = stream.Position;
            }
        }

        private void ProcessLogLine(string line)
        {
            // Check for connection to a login server (character select screen or client start)
            if (line.Contains("Async connecting to ", StringComparison.Ordinal) && line.Contains("login.pathofexile", StringComparison.Ordinal))
            {
                _pendingLoginScreen = true;
                System.Diagnostics.Debug.WriteLine("Connection to login server detected. Flagged pending character select.");
            }

            // Check for connection to an instance server (character select complete and loading into game)
            if (line.Contains("Connecting to instance server at", StringComparison.Ordinal))
            {
                if (_pendingLoginScreen)
                {
                    _pendingLoginScreen = false;
                    System.Diagnostics.Debug.WriteLine("Instance server connection detected after login. Triggering background character history scan.");
                    Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        TriggerHistoryScan();
                    });
                }
            }

            // Check for area-level generation (zone level, not character level)
            var areaLvlMatch = AreaLevelRegex().Match(line);
            if (areaLvlMatch.Success && int.TryParse(areaLvlMatch.Groups[1].Value, out int lvl))
            {
                _lastGeneratedLevel = lvl;
            }

            // Track the active character (logged on each instance load).
            var activeMatch = ActiveCharRegex().Match(line);
            if (activeMatch.Success)
            {
                HandleActiveCharacterLine(activeMatch);
            }

            // Check for character level-up (always update the active character on level-up).
            var charLvlMatch = CharLevelRegex().Match(line);
            if (charLvlMatch.Success)
            {
                HandleCharacterLevelUpLine(charLvlMatch);
            }

            // Check for zone transition
            var zoneMatch = ZoneRegex().Match(line);
            if (zoneMatch.Success && !string.Equals(zoneMatch.Groups[1].Value, _nullSceneSource, StringComparison.Ordinal))
            {
                HandleZoneTransitionLine(zoneMatch);
            }
        }

        private void HandleActiveCharacterLine(Match activeMatch)
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

        private void HandleCharacterLevelUpLine(Match charLvlMatch)
        {
            string charName = charLvlMatch.Groups[1].Value.Trim();
            if (int.TryParse(charLvlMatch.Groups[2].Value, out int charLevel))
            {
                _currentCharacterName = StripClass(charName);
                System.Diagnostics.Debug.WriteLine($"Detected level-up: {charName} is now level {charLevel}");
                PlayerLevelChanged?.Invoke(this, new PlayerLevelChangedEventArgs(_currentCharacterName, charLevel));
            }
        }

        private void HandleZoneTransitionLine(Match zoneMatch)
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

        /// <summary>
        /// Reads the tail of the log file to find the most recent character level-up
        /// and zone entry, then fires events so the UI starts with accurate data.
        /// </summary>
        private void ScanRecentHistory()
        {
            ScanCharacterHistory();
            ScanZoneHistory();
        }

        private void ScanCharacterHistory()
        {
            try
            {
                var (detectedName, detectedLevel) = DetectActiveCharacter();
                if (!string.IsNullOrEmpty(detectedName))
                {
                    _currentCharacterName = detectedName;
                    System.Diagnostics.Debug.WriteLine($"Restored active character from log analysis: {detectedName} level {detectedLevel}");
                    PlayerLevelChanged?.Invoke(this, new PlayerLevelChangedEventArgs(detectedName, detectedLevel));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning character history: {ex.Message}");
            }
        }

        private (string? Name, int Level) DetectActiveCharacter()
        {
            // 1. Try to find based on active build class synonyms
            var synonyms = ActiveBuildClassSynonymsProvider?.Invoke();
            if (synonyms?.Length > 0)
            {
                var result = MatchCharacterByClassSynonyms(synonyms);
                if (result.Name != null) return result;
            }

            // 2. Fallback to the most recent level-up of ANY character in the log file
            var anyMatch = FindLatestLineMatch(AnyLevelRegex());
            if (anyMatch != null && int.TryParse(anyMatch.Groups[3].Value, out int anyLvl))
            {
                string name = anyMatch.Groups[1].Value.Trim();
                System.Diagnostics.Debug.WriteLine($"Fallback to most recent level-up of any character: {name} level {anyLvl}");
                return (name, anyLvl);
            }

            // 3. Fallback to the warning-based active character regex if still not resolved
            var nameMatch = FindLatestLineMatch(ActiveCharRegex());
            string? activeName = nameMatch?.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(activeName))
            {
                int lvl = FindLatestLevelForCharacter(activeName);
                System.Diagnostics.Debug.WriteLine($"Fallback to active character warning: {activeName} level {lvl}");
                return (activeName, lvl);
            }

            return (null, 0);
        }

        private (string? Name, int Level) MatchCharacterByClassSynonyms(string[] synonyms)
        {
            string escapedSynonyms = string.Join("|", Array.ConvertAll(synonyms, Regex.Escape));

            if (!string.Equals(escapedSynonyms, _cachedSynonymsKey, StringComparison.Ordinal))
            {
                _cachedSynonymsKey = escapedSynonyms;
                _cachedClassLevelRegex = new Regex(@"] : (\S+) \((?:" + escapedSynonyms + @")\) is now level (\d+)", RegexOptions.Compiled);
            }

            var match = FindLatestLineMatch(_cachedClassLevelRegex!);
            if (match != null && int.TryParse(match.Groups[2].Value, out int lvl))
            {
                string name = match.Groups[1].Value.Trim();
                System.Diagnostics.Debug.WriteLine($"Found active character matching build class synonyms ({string.Join(",", synonyms)}): {name} level {lvl}");
                return (name, lvl);
            }
            return (null, 0);
        }

        private void ScanZoneHistory()
        {
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
                    var areaMatch = AreaLevelRegex().Match(line);
                    if (areaMatch.Success && int.TryParse(areaMatch.Groups[1].Value, out int aLvl))
                    {
                        lastAreaLevel = aLvl;
                    }

                    var zoneMatch = ZoneRegex().Match(line);
                    if (zoneMatch.Success && !string.Equals(zoneMatch.Groups[1].Value, _nullSceneSource, StringComparison.Ordinal))
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
                    return matches[^1]; // last match = most recent in this chunk
                }

                // Carry this chunk's incomplete first line to the next (earlier) chunk.
                int firstNl = text.IndexOf('\n', StringComparison.Ordinal);
                carry = firstNl >= 0 ? text[..firstNl] : text;
            }

            return null;
        }

        // "Light_Fisted (Martial Artist)" -> "Light_Fisted"
        private static string StripClass(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name[..paren] : name;
        }

        private static readonly (string Substring, int Level)[] _zoneLevels = [
            ("oakhaven", 1),
            ("lioneye's watch", 1),
            ("the coast", 2),
            ("mud flats", 3),
            ("the grotto", 4),
            ("the ridge", 5),
            ("great forest", 6),
            ("hooded copse", 7),
            ("riverways", 8),
            ("oasis", 10),
            ("forest encampment", 16),
            ("fields", 16),
            ("ruins", 18),
            ("sarn encampment", 33),
            ("slums", 33)
        ];

        public static int GetZoneLevelFromName(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName)) return 1;

            foreach (var (sub, zoneLvl) in _zoneLevels)
            {
                if (zoneName.Contains(sub, StringComparison.OrdinalIgnoreCase))
                {
                    return zoneLvl;
                }
            }

            var levelInNameMatch = LevelInNameRegex().Match(zoneName);
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
