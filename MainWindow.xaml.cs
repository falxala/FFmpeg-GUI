using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics; // Process クラスを使用するため

namespace FfmpegInstallerApp
{
    public partial class MainWindow : Window
    {
        // FFmpegのダウンロードURL (Gyan's Builds の full 版は7z形式)
        private const string FFMPEG_DOWNLOAD_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z";
        // アプリケーションの実行ディレクトリからのFFmpegルートディレクトリの相対パス
        private const string FFMPEG_ROOT_DIR_RELATIVE = "ffmpeg";
        // FFmpegルートディレクトリからのffmpeg.exeの相対パス
        private const string FFMPEG_EXE_RELATIVE_PATH = "bin\\ffmpeg.exe";
        // アプリケーションの実行ディレクトリからの7za.exeの相対パス
        private const string SEVEN_ZA_EXE_RELATIVE_PATH = "Tools\\7za.exe";

        // ffmpeg.exe が存在するディレクトリのフルパス
        private string _ffmpegBinDirectory;
        // ffmpeg.exe のフルパス
        private string _ffmpegExePath;
        // 7za.exe のフルパス
        private string _sevenZaExePath;

        public MainWindow()
        {
            InitializeComponent();

            // パスを初期化
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _ffmpegBinDirectory = Path.Combine(appDirectory, FFMPEG_ROOT_DIR_RELATIVE, "bin");
            _ffmpegExePath = Path.Combine(_ffmpegBinDirectory, "ffmpeg.exe");
            _sevenZaExePath = Path.Combine(appDirectory, SEVEN_ZA_EXE_RELATIVE_PATH);

            // アプリ起動時にFFmpegの存在チェックとダウンロードを非同期で実行
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckInitialState();
        }

        private async Task CheckInitialState()
        {
            DownloadProgressBar.Visibility = Visibility.Collapsed; // プログレスバーを非表示

            StatusTextBlock.Text = "必要なツールの確認をしています...";

            // 7za.exe の存在確認を最初に行う
            if (!File.Exists(_sevenZaExePath))
            {
                StatusTextBlock.Text = $"エラー: 圧縮解除ツールが見つかりません。\n" +
                                       $"7-Zipのコマンドラインバージョン(7za.exe)をダウンロードし、\n" +
                                       $"アプリケーションの実行ディレクトリ内の 'Tools' フォルダに配置してください。\n" +
                                       $"期待されるパス: '{Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SEVEN_ZA_EXE_RELATIVE_PATH)}'";
                MessageBox.Show($"'{SEVEN_ZA_EXE_RELATIVE_PATH}' が見つかりません。アプリケーションを終了します。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown(); // 7za.exe がなければ処理できないため、アプリを終了
                return;
            }

            StatusTextBlock.Text = "FFmpegの存在を確認しています...";

            if (File.Exists(_ffmpegExePath))
            {
                StatusTextBlock.Text = $"FFmpegが見つかりました: {_ffmpegExePath}\n変換機能を使用できます。";
                // FFmpegが存在するので、特にユーザーへの問いかけは不要
            }
            else
            {
                // FFmpegが見つからない場合、ダイアログでダウンロードを問い合わせる
                MessageBoxResult result = MessageBox.Show(
                    "FFmpegが見つかりませんでした。今すぐダウンロードして配置しますか？",
                    "FFmpegのダウンロード確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    StatusTextBlock.Text = $"FFmpegをダウンロード＆配置します...";
                    try
                    {
                        await DownloadAndExtractFfmpeg(); // ダウンロード処理を呼び出す
                        StatusTextBlock.Text = "FFmpegのダウンロードと配置が完了しました！\n変換機能を使用できます。";
                    }
                    catch (Exception ex)
                    {
                        StatusTextBlock.Text = $"FFmpegのダウンロード中にエラーが発生しました: {ex.Message}";
                        MessageBox.Show($"エラー: {ex.Message}\n詳細: {ex.StackTrace}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                        Application.Current.Shutdown(); // ダウンロード失敗時はアプリを終了
                    }
                }
                else
                {
                    StatusTextBlock.Text = "FFmpegがダウンロードされませんでした。アプリケーションを終了します。";
                    MessageBox.Show("FFmpegがないため、アプリケーションは機能しません。", "終了", MessageBoxButton.OK, MessageBoxImage.Information);
                    Application.Current.Shutdown(); // ダウンロードしない場合はアプリを終了
                }
            }
        }

        // DownloadButton_Click メソッドは不要になったので削除しました

        private async Task DownloadAndExtractFfmpeg()
        {
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string downloadFilePath = Path.Combine(appDirectory, "ffmpeg.7z");
            string extractDirectory = Path.Combine(appDirectory, FFMPEG_ROOT_DIR_RELATIVE);

            // 既存のFFmpegディレクトリがあれば削除
            if (Directory.Exists(extractDirectory))
            {
                StatusTextBlock.Text = $"既存のFFmpegディレクトリ ({extractDirectory}) を削除しています...";
                Directory.Delete(extractDirectory, true);
                await Task.Delay(100);
            }
            Directory.CreateDirectory(extractDirectory);

            // ダウンロード
            StatusTextBlock.Text = $"FFmpegをダウンロード中: {FFMPEG_DOWNLOAD_URL}...";
            DownloadProgressBar.Value = 0;
            DownloadProgressBar.Visibility = Visibility.Visible;

            using (HttpClient client = new HttpClient())
            {
                using (var response = await client.GetAsync(FFMPEG_DOWNLOAD_URL, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength;
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    {
                        using (var fileStream = new FileStream(downloadFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
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
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        DownloadProgressBar.Value = progress;
                                        StatusTextBlock.Text = $"ダウンロード中: {progress:F2}% ({receivedBytes}/{totalBytes.Value} バイト)";
                                    });
                                }
                            }
                        }
                    }
                }
            }
            DownloadProgressBar.Visibility = Visibility.Collapsed;

            // 解凍 (7za.exe を使用)
            StatusTextBlock.Text = "FFmpegを解凍中 (7za.exeを使用)...";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _sevenZaExePath,
                    Arguments = $"x \"{downloadFilePath}\" -o\"{extractDirectory}\" -y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using (var process = Process.Start(startInfo))
                {
                    //nullチェック
                    if (process == null)
                    {
                        throw new Exception($"圧縮解除ツール '{SEVEN_ZA_EXE_RELATIVE_PATH}' の起動に失敗しました。Process が null です。");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    await process.WaitForExitAsync();

                    string output = await outputTask;
                    string error = await errorTask;

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"圧縮解除ツール '{SEVEN_ZA_EXE_RELATIVE_PATH}' の実行中にエラーが発生しました。\n出力:\n{output}\nエラー:\n{error}");
                    }
                    StatusTextBlock.Text += $"\n圧縮解除ツール出力:\n{output}";
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"圧縮解除ツール '{SEVEN_ZA_EXE_RELATIVE_PATH}' の実行に失敗しました。", ex);
            }

            // ダウンロードした7zファイルは不要なので削除
            if (File.Exists(downloadFilePath))
            {
                File.Delete(downloadFilePath);
            }

            // 抽出されたディレクトリの中にさらにディレクトリがある場合があるので、中身をffmpegディレクトリ直下に移動する
            string[] extractedSubDirs = Directory.GetDirectories(extractDirectory);
            if (extractedSubDirs.Length == 1)
            {
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
                        if (Directory.Exists(destPath))
                        {
                            Directory.Delete(destPath, true);
                        }
                        Directory.Move(entryPath, destPath);
                    }
                }
                Directory.Delete(innerDir, true); // 空になった内側のディレクトリを削除
            }

            // FFmpegのパスを更新（ダウンロード後の最新状態）
            if (File.Exists(_ffmpegExePath))
            {
                StatusTextBlock.Text += $"\nffmpeg.exe が '{_ffmpegExePath}' に見つかりました。";
            }
            else
            {
                StatusTextBlock.Text += $"\nffmpeg.exe が '{_ffmpegExePath}' に見つかりませんでした。ディレクトリ構造を確認してください。";
            }
        }
    }
}