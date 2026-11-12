using System;
using System.IO;
using System.Linq;
using System.Text;

namespace GlassLingo.Logging
{
    /// <summary>
    /// 极简文件日志。按天分文件，写在 %LOCALAPPDATA%\glasslingo\logs\。
    /// 不引第三方库——P0 定的「能不用第三方就不用」。
    /// 日志本身出错绝不能影响主流程，所以全部 try/catch 吞掉。
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string? _dir;
        private static DateTime _lastCleanup;
        internal const long MaxFileBytes = 2 * 1024 * 1024;
        internal static void Maintain(string directory, string current, DateTime now)
        {
            var active = new FileInfo(current);
            if (active.Exists && active.Length >= MaxFileBytes)
                File.Move(current, current + ".previous", overwrite: true);
            long total = 0;
            foreach (var file in new DirectoryInfo(directory).GetFiles("glasslingo-*.log*").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                // Restrict cleanup to this application's date-based log names.
                if (!System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^glasslingo-\d{8}\.log(?:\.previous)?$")) continue;
                total += file.Length;
                if (file.FullName == Path.GetFullPath(current)) continue;
                if (file.LastWriteTimeUtc < now.ToUniversalTime().AddDays(-14) || total > 20 * 1024 * 1024) file.Delete();
            }
        }

        private static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                try
                {
                    var baseDir = Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData);
                    _dir = Path.Combine(baseDir, "glasslingo", "logs");
                    Directory.CreateDirectory(_dir);
                }
                catch
                {
                    _dir = Path.GetTempPath();
                }
                return _dir;
            }
        }

        public static string CurrentFile =>
            Path.Combine(Dir, $"glasslingo-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string msg) => Write("INFO ", msg, null);
        public static void Warn(string msg) => Write("WARN ", msg, null);
        public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);

        private static void Write(string level, string msg, Exception? ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(" [").Append(level).Append("] ");
                sb.Append(msg);
                if (ex != null)
                {
                    sb.AppendLine();
                    sb.Append(ex.ToString());
                }
                lock (Gate)
                {
                    if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromMinutes(1))
                    { Maintain(Dir, CurrentFile, DateTime.UtcNow); _lastCleanup = DateTime.UtcNow; }
                    File.AppendAllText(CurrentFile, sb.ToString() + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志写不进去也不能崩程序
            }
        }
    }
}
