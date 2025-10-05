// Services/FfmpegManager.cs

using ffmpeg.Models;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ffmpeg.Services
{
    public class FfmpegManager
    {
        // 定数 (変更なし)
        private const string FFMPEG_DOWNLOAD_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z";
        private const string FFMPEG_ROOT_DIR_RELATIVE = "ffmpeg";
        private const string FFMPEG_EXE_RELATIVE_PATH = "bin\\ffmpeg.exe";
        private const string SEVEN_ZA_EXE_RELATIVE_PATH = "Tools\\7za.exe";

        // プロパティ (変更なし)
        public string FfmpegExePath { get; }
        public string FfprobeExePath { get; }
        public string SevenZaExePath { get; }
        private string AppDirectory { get; }

        public FfmpegManager()
        {
            AppDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegBinDir = Path.Combine(AppDirectory, FFMPEG_ROOT_DIR_RELATIVE, "bin");
            FfmpegExePath = Path.Combine(ffmpegBinDir, "ffmpeg.exe");
            FfprobeExePath = Path.Combine(ffmpegBinDir, "ffprobe.exe");
            SevenZaExePath = Path.Combine(AppDirectory, SEVEN_ZA_EXE_RELATIVE_PATH);
        }

        public bool CheckSevenZaExists() => File.Exists(SevenZaExePath);
        public bool CheckFfmpegExists() => File.Exists(FfmpegExePath) && File.Exists(FfprobeExePath);

        // DownloadAndExtractFfmpegAsync (ステータス進捗報告をMainWindowに移管、ダウンロード進捗は残す)
        public async Task DownloadAndExtractFfmpegAsync(IProgress<string> statusProgress, IProgress<double> downloadProgress)
        {
            string downloadFilePath = Path.Combine(AppDirectory, "ffmpeg.7z");
            string extractDirectory = Path.Combine(AppDirectory, FFMPEG_ROOT_DIR_RELATIVE);

            if (Directory.Exists(extractDirectory))
            {
                statusProgress.Report($"既存のFFmpegディレクトリを削除しています...");
                Directory.Delete(extractDirectory, true);
                await Task.Delay(100);
            }
            Directory.CreateDirectory(extractDirectory);

            // ダウンロード
            statusProgress.Report($"FFmpegをダウンロード中...");
            using (var client = new HttpClient())
            {
                using var response = await client.GetAsync(FFMPEG_DOWNLOAD_URL, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(downloadFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                long receivedBytes = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    receivedBytes += bytesRead;
                    if (totalBytes.HasValue)
                    {
                        double progress = (double)receivedBytes / totalBytes.Value * 100;
                        downloadProgress.Report(progress);
                    }
                }
            }
            downloadProgress.Report(0); // プログレスバーをリセット

            // 解凍
            statusProgress.Report("FFmpegを解凍中...");
            await RunSevenZaAsync(downloadFilePath, extractDirectory);

            if (File.Exists(downloadFilePath)) File.Delete(downloadFilePath);

            // ディレクトリ構造の調整
            string[] extractedSubDirs = Directory.GetDirectories(extractDirectory);
            if (extractedSubDirs.Length == 1)
            {
                string innerDir = extractedSubDirs[0];
                foreach (string entryPath in Directory.GetFileSystemEntries(innerDir))
                {
                    string entryName = Path.GetFileName(entryPath);
                    string destPath = Path.Combine(extractDirectory, entryName);
                    if (File.Exists(entryPath)) File.Move(entryPath, destPath);
                    else if (Directory.Exists(entryPath)) Directory.Move(entryPath, destPath);
                }
                Directory.Delete(innerDir, true);
            }
        }

        private async Task RunSevenZaAsync(string archivePath, string destinationPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = SevenZaExePath,
                Arguments = $"x \"{archivePath}\" -o\"{destinationPath}\" -y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("7za.exeの起動に失敗しました。");

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                string error = await process.StandardError.ReadToEndAsync();
                throw new Exception($"7za.exeの実行に失敗しました: {error}");
            }
        }

        // IProgress<T> を削除し、Action<string> を受け取るように変更
        public async Task ConvertFileAsync(
            SourceFileInfo fileToConvert,
            string outputPath,
            Action<string> outputLogCallback, // FFmpegの生出力を報告するためのコールバック
            CancellationToken cancellationToken)
        {
            if (!CheckFfmpegExists())
            {
                throw new FileNotFoundException("ffmpeg.exeが見つかりません。");
            }

            bool IsValidCodec(string? codec)
            {
                if (string.IsNullOrEmpty(codec)) return false;
                if (codec == "N-A") return false;
                if (codec.Contains("Error") || codec.Contains("Timeout") || codec.Contains("Failed") || codec.Contains("Invalid") || codec.Contains("Bad") || codec.Contains("No Data")) return false;
                return true;
            }

            bool hasVideo = IsValidCodec(fileToConvert.VideoCodec);
            bool hasAudio = IsValidCodec(fileToConvert.AudioCodec);

            if (!hasVideo && !hasAudio)
            {
                throw new Exception("有効な映像または音声ストリームが見つかりませんでした。ファイルが破損しているか、サポートされていない形式の可能性があります。");
            }

            var arguments = new StringBuilder();
            // 入力ファイルを指定
            arguments.Append($"-i \"{fileToConvert.FullPath}\" ");

            // ビデオオプション
            if (hasVideo)
            {
                arguments.Append("-c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p ");
            }
            else
            {
                arguments.Append("-vn "); // 映像ストリームなし
            }

            // オーディオオプション
            if (hasAudio)
            {
                arguments.Append("-c:a aac -b:a 192k ");
            }
            else
            {
                arguments.Append("-an "); // 音声ストリームなし
            }

            // 出力ファイルを指定し、既存ファイルを上書き
            arguments.Append($"\"{outputPath}\" -y");

            string fullArguments = arguments.ToString();

            outputLogCallback($"--- {fileToConvert.FileName} の変換開始 ---{Environment.NewLine}");
            outputLogCallback($"コマンド: ffmpeg {fullArguments}{Environment.NewLine}");

            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegExePath,
                Arguments = fullArguments,
                RedirectStandardOutput = false, // 標準出力は通常使わない
                RedirectStandardError = true,   // FFmpegのログは標準エラーに出力される
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            var tcs = new TaskCompletionSource<int>();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch (Exception) { /* プロセスが既に終了している場合などのエラーは無視 */ }
                tcs.TrySetCanceled(cancellationToken);
            });

            process.Exited += (sender, args) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    tcs.TrySetResult(process.ExitCode);
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;
                // ここでFFmpegの生出力を直接コールバックに渡す
                outputLogCallback($"{args.Data}{Environment.NewLine}");
            };

            process.Start();
            process.BeginErrorReadLine();

            await tcs.Task; // プロセス終了またはキャンセルまで待機

            process.WaitForExit(); // プロセスが完全に終了するまで待機

            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                throw new Exception($"FFmpegエラー (Exit Code: {process.ExitCode}). 詳細についてはログを確認してください。");
            }
            outputLogCallback($"--- {fileToConvert.FileName} の変換終了 (Exit Code: {process.ExitCode}) ---{Environment.NewLine}{Environment.NewLine}");
        }

        // FfprobeFileAsync は変更なし
        public async Task<SourceFileInfo?> ProbeFileAsync(string filePath)
        {
            if (!CheckFfmpegExists()) return null;

            string arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"";
            var startInfo = new ProcessStartInfo
            {
                FileName = FfprobeExePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string jsonOutput;
            string errorOutput;

            try
            {
                Task<string> readOutputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> readErrorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(cts.Token);

                jsonOutput = await readOutputTask;
                errorOutput = await readErrorTask;
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(); } catch { /* 無視 */ }
                return new SourceFileInfo { Duration = "Timeout", Container = "Timeout", VideoCodec = "Timeout", AudioCodec = "Timeout", RawDurationSeconds = 0 };
            }

            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errorOutput))
            {
                return new SourceFileInfo { Duration = "Probe Failed", Container = "Error", VideoCodec = "Invalid File", AudioCodec = "Invalid File", RawDurationSeconds = 0 };
            }

            if (string.IsNullOrWhiteSpace(jsonOutput))
            {
                return new SourceFileInfo { Duration = "Probe Failed", Container = "Error", VideoCodec = "No Data", AudioCodec = "No Data", RawDurationSeconds = 0 };
            }

            try
            {
                if (jsonOutput.Contains("\"tags\""))
                {
                    jsonOutput = Regex.Replace(jsonOutput, @",\s*""tags""\s*:\s*\{.*?\}\s*(?=\})", "", RegexOptions.Singleline);
                    jsonOutput = Regex.Replace(jsonOutput, @"""tags""\s*:\s*\{.*?\}\s*,?", "", RegexOptions.Singleline);
                }

                var ffprobeData = JsonConvert.DeserializeObject<FfprobeOutput>(jsonOutput);

                if (ffprobeData == null)
                {
                    throw new JsonException("Deserialized ffprobe data is null.");
                }

                var probedInfo = new SourceFileInfo();
                probedInfo.Container = ffprobeData.Format?.FormatName ?? "N/A";

                if (double.TryParse(ffprobeData.Format?.Duration, NumberStyles.Any, CultureInfo.InvariantCulture, out double durationSeconds))
                {
                    probedInfo.RawDurationSeconds = durationSeconds;
                    probedInfo.Duration = TimeSpan.FromSeconds(durationSeconds).ToString(@"hh\:mm\:ss");
                }
                else
                {
                    probedInfo.RawDurationSeconds = 0;
                    probedInfo.Duration = "N/A";
                }

                probedInfo.VideoCodec = ffprobeData.Streams?.FirstOrDefault(s => s.CodecType == "video")?.CodecName ?? "N/A";
                probedInfo.AudioCodec = ffprobeData.Streams?.FirstOrDefault(s => s.CodecType == "audio")?.CodecName ?? "N/A";

                return probedInfo;
            }
            catch (JsonException)
            {
                return new SourceFileInfo { Duration = "Parse Failed", Container = "Error", VideoCodec = "Bad Format", AudioCodec = "Bad Format", RawDurationSeconds = 0 };
            }
        }
    }
}