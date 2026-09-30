// FILE: jigu-app/src/PayloadExt.cs
// 安装包负载的自解压实现（与 build.ps1 生成的 InstallerPayload 类配合）。
// 用途：
//   1. 安装向导把全部文件释放到用户选择的安装目录；
//   2. 热更新时新的安装包以 --extract-to <目录> 运行，覆盖旧文件。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jigu
{
    internal static partial class InstallerPayload
    {
        /// <summary>
        /// 解压时要保留、不覆盖的文件（目标目录里已存在则跳过）。
        /// 更新源配置是「分发方或用户自己改的」，默认值在编译内（Net/UpdateSource），
        /// 所以自动更新把安装目录里的这几个文件盖回默认值，等于把自定义源静默重置 ——
        /// 对自动更新是致命的。首次安装时目录里没有这些文件，照常写入默认值。
        /// </summary>
        private static readonly HashSet<string> PreserveIfPresent = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) { "update_base.txt", "data_base.txt" };

        public static int ExtractTo(string destDir)
        {
            int failed;
            return ExtractTo(destDir, out failed);
        }

        /// <summary>
        /// 把负载里的每个文件写入 destDir。返回写入成功的数量，failed 为失败的数量。
        ///
        /// 逐文件隔离故障：某个文件写不动（被占用、磁盘满、负载项坏掉）绝不能连累后面的。
        /// 早期版本把整个 foreach 裹在一个 try 里 —— 一个文件抛异常就静默丢掉剩下的全部，
        /// 而调用方只看「写成功了几个 > 0」就报成功，用户拿到半新半旧的安装目录还以为更新好了。
        /// 调用方必须同时看 failed。
        /// </summary>
        public static int ExtractTo(string destDir, out int failed)
        {
            failed = 0;
            if (string.IsNullOrEmpty(destDir)) return 0;

            Dictionary<string, string> table = Table();
            int n = 0;
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            }
            catch (Exception ex)
            {
                // 连目录都建不出来：整个负载一个也没写，如实报告
                Log.Error("ExtractTo 建目录 " + destDir, ex);
                failed = table.Count;
                return 0;
            }

            foreach (KeyValuePair<string, string> kv in table)
            {
                try
                {
                    string rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                    // 负载键来自构建期打包，理应是干净的；万一哈希门被绕过，也不允许越出 destDir
                    if (rel.IndexOf("..", StringComparison.Ordinal) >= 0 || Path.IsPathRooted(rel))
                    {
                        failed++;
                        Log.Write("ExtractTo 拒绝越界条目: " + kv.Key);
                        continue;
                    }
                    string path = Path.Combine(destDir, rel);
                    if (PreserveIfPresent.Contains(rel) && File.Exists(path)) continue;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    byte[] bytes = Convert.FromBase64String(kv.Value);

                    // 原子写入，避免覆盖过程中被占用导致半截文件
                    string tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch
                        {
                            // 文件被占用（例如正在运行的主程序）：先改名再写入
                            string bak = path + ".old";
                            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                            try { File.Move(path, bak); } catch { }
                        }
                    }
                    File.Move(tmp, path);
                    n++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error("ExtractTo " + kv.Key, ex);
                }
            }
            if (failed > 0)
                Log.Write("ExtractTo: 写入 " + n + " 个，失败 " + failed + " 个（目标 " + destDir + "）");
            return n;
        }
    }
}
