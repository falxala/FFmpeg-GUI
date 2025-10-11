// Services/FfmpegManager.cs

using ffmpeg.Models;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ffmpeg.Services
{
    public class FfmpegManager
    {
        private const string FFMPEG_ROOT_DIR_RELATIVE = "ffmpeg";
        private const string SEVEN_ZA_EXE_RELATIVE_PATH = "Tools\\7za.exe";

        public string? FfmpegExePath { get; private set; }
        public string? FfprobeExePath { get; private set; }
        public string SevenZaExePath { get; }
        private string AppDirectory { get; }

        public FfmpegManager()
        {
            AppDirectory = AppDomain.CurrentDomain.BaseDirectory;
            SevenZaExePath = Path.Combine(AppDirectory, SEVEN_ZA_EXE_RELATIVE_PATH);
        }

        /// <summary>
        /// アプリケーションディレクトリ内、または環境変数PATHからFFmpegを探します。
        /// </summary>
        /// <returns>見つかったffmpeg.exeのパス。見つからない場合はnull。</returns>
        public string? LocateFfmpeg()
        {
            // 1. アプリケーションのサブディレクトリ内をチェック (推奨)
            string localFfmpegDir = Path.Combine(AppDirectory, FFMPEG_ROOT_DIR_RELATIVE, "bin");
            string localFfmpegPath = Path.Combine(localFfmpegDir, "ffmpeg.exe");
            string localFfprobePath = Path.Combine(localFfmpegDir, "ffprobe.exe");

            if (File.Exists(localFfmpegPath) && File.Exists(localFfprobePath))
            {
                FfmpegExePath = localFfmpegPath;
                FfprobeExePath = localFfprobePath;
                return FfmpegExePath;
            }

            // 2. 環境変数 PATH をチェック
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (pathVariable != null)
            {
                foreach (var path in pathVariable.Split(Path.PathSeparator))
                {
                    try
                    {
                        string potentialFfmpegPath = Path.Combine(path, "ffmpeg.exe");
                        string potentialFfprobePath = Path.Combine(path, "ffprobe.exe");
                        if (File.Exists(potentialFfmpegPath) && File.Exists(potentialFfprobePath))
                        {
                            FfmpegExePath = potentialFfmpegPath;
                            FfprobeExePath = potentialFfprobePath;
                            return FfmpegExePath;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Path.Combineで無効なパス文字が含まれている場合など
                    }
                }
            }

            // 見つからなかった場合
            FfmpegExePath = null;
            FfprobeExePath = null;
            return null;
        }


        public bool CheckSevenZaExists() => File.Exists(SevenZaExePath);
        public bool CheckFfmpegExists() => !string.IsNullOrEmpty(FfmpegExePath) && !string.IsNullOrEmpty(FfprobeExePath) && File.Exists(FfmpegExePath) && File.Exists(FfprobeExePath);

        /// <summary>
        /// ユーザーが指定したアーカイブファイルを解凍し、アプリケーションフォルダ内に配置します。
        /// </summary>
        public async Task ExtractFfmpegArchiveAsync(string archivePath, IProgress<string> statusProgress)
        {
            string extractDirectory = Path.Combine(AppDirectory, FFMPEG_ROOT_DIR_RELATIVE);

            if (Directory.Exists(extractDirectory))
            {
                statusProgress.Report($"既存のFFmpegディレクトリを削除しています...");
                Directory.Delete(extractDirectory, true);
                await Task.Delay(100); // UIが更新される猶予
            }
            Directory.CreateDirectory(extractDirectory);

            statusProgress.Report("アーカイブを解凍中...");
            await RunSevenZaAsync(archivePath, extractDirectory);

            // 解凍後のディレクトリ整理
            // gyan.devのビルドは 'ffmpeg-xxxx-full_build' のような一段深いフォルダが作られるため、中身をルートに移動させる
            string[] extractedSubDirs = Directory.GetDirectories(extractDirectory);
            if (extractedSubDirs.Length == 1)
            {
                statusProgress.Report("ファイルを移動中...");
                string innerDir = extractedSubDirs[0];
                foreach (string entryPath in Directory.GetFileSystemEntries(innerDir))
                {
                    string entryName = Path.GetFileName(entryPath);
                    string destPath = Path.Combine(extractDirectory, entryName);
                    if (File.Exists(entryPath))
                    {
                        File.Move(entryPath, destPath);
                    }
                    else if (Directory.Exists(entryPath))
                    {
                        Directory.Move(entryPath, destPath);
                    }
                }
                Directory.Delete(innerDir, true);
            }
            statusProgress.Report("展開完了。");
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

        public async Task ConvertFileAsync(
                    SourceFileInfo fileToConvert,
                    string outputPath,
                    Action<string> outputLogCallback,
                    CancellationToken cancellationToken)
        {
            if (!CheckFfmpegExists())
            {
                throw new FileNotFoundException("ffmpeg.exeまたはffprobe.exeが見つかりません。アプリケーションを再起動して設定してください。");
            }

            // ... (以降の処理は変更なし)
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
            arguments.Append("-progress pipe:2 ");

            arguments.Append($"-i \"{fileToConvert.FullPath}\" ");
            if (hasVideo) arguments.Append("-c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p ");
            else arguments.Append("-vn ");
            if (hasAudio) arguments.Append("-c:a aac -b:a 192k ");
            else arguments.Append("-an ");
            arguments.Append($"\"{outputPath}\" -y");
            string fullArguments = arguments.ToString();

            outputLogCallback($"--- {fileToConvert.FileName} の変換開始 ---{Environment.NewLine}");
            outputLogCallback($"コマンド: {Path.GetFileName(FfmpegExePath)} {fullArguments}{Environment.NewLine}");

            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegExePath,
                Arguments = fullArguments,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>();

            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                tcs.TrySetCanceled(cancellationToken);
            });

            process.Exited += (sender, args) =>
            {
                tcs.TrySetResult(cancellationToken.IsCancellationRequested ? -1 : process.ExitCode);
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    outputLogCallback($"{args.Data}{Environment.NewLine}");
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            await tcs.Task;

            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                throw new Exception($"FFmpegエラー (Exit Code: {process.ExitCode}). 詳細についてはログを確認してください。");
            }
            outputLogCallback($"--- {fileToConvert.FileName} の変換終了 (Exit Code: {process.ExitCode}) ---{Environment.NewLine}{Environment.NewLine}");
        }

        public async Task<SourceFileInfo?> ProbeFileAsync(string filePath)
        {
            if (!CheckFfmpegExists()) return null;

            // ... (以降の処理は変更なし)
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
                try { if (!process.HasExited) process.Kill(); } catch { }
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
                if (ffprobeData == null) throw new JsonException("Deserialized ffprobe data is null.");

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