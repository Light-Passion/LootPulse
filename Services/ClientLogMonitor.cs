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

        public ZoneChangedEventArgs(string zoneName)
        {
            ZoneName = zoneName;
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
                                        var match = ZoneRegex.Match(line);
                                        if (match.Success)
                                        {
                                            var zoneName = match.Groups[1].Value.Trim();
                                            System.Diagnostics.Debug.WriteLine($"Detected zone transition: {zoneName}");
                                            ZoneChanged?.Invoke(this, new ZoneChangedEventArgs(zoneName));
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
    }
}
