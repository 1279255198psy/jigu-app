// FILE: jigu-app/src/PayloadExt.cs
// 安装包负载的自解压实现（负载由 build.ps1 作为 /resource: 装进装配，见 Table）。
// 用途：
//   1. 安装向导把全部文件释放到用户选择的安装目录；
//   2. 热更新时新的安装包以 --extract-to <目录> 运行，覆盖旧文件。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Jigu
{
    internal static partial class InstallerPayload
    {
        /// <summary>史书分片资源的清单资源名（由 build.ps1 的 /resource: 装入）</summary>
        public const string ShardResourceName = "Jigu.corpus.shards.zip";

        /// <summary>安装负载资源的清单资源名（同样是 build.ps1 的 /resource:）</summary>
        public const string PayloadResourceName = "Jigu.payload.zip";

        private static Dictionary<string, byte[]> _table;
        private static readonly object _tableGate = new object();

        /// <summary>
        /// 负载清单：相对路径（正斜杠分隔）-> 文件内容。只解一次，之后走缓存。
        ///
        /// 0.4.0 之前这张表是 build.ps1 生成的一整份 base64 字面量。字面量在程序集里按
        /// UTF-16 存放，base64 之后再翻一倍（实测 2.67×），而且要记在 csc 的**用户字符串堆**
        /// 上 —— 那是个硬顶。0.4.0 的 CI 就是这样炸的：负载因新版 WebView2 SDK 长了 877 KB，
        /// 与 45 MB 的分片资源同一次编译，csc 直接报「已无逻辑空间可创建更多用户字符串」
        /// （CS0013）。同一份源码在装着旧 SDK 的机器上编得过、在 CI 上编不过 —— 典型的
        /// 「差一个参数」形状，靠调负载大小是治不好的，只会越来越近。
        ///
        /// 所以与分片同走 /resource: 的 zip：不占字符串堆，按 deflate 后的体积算。
        /// 资源缺席时（只编 src\*.cs 的编译检查、或没带负载的旧装配）返回空表而不是抛异常，
        /// 与 ExtractShards 的做法一致。
        /// </summary>
        public static Dictionary<string, byte[]> Table()
        {
            lock (_tableGate)
            {
                if (_table != null) return _table;
                Dictionary<string, byte[]> t = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Stream res = typeof(InstallerPayload).Assembly
                        .GetManifestResourceStream(PayloadResourceName);
                    if (res == null)
                    {
                        Log.Write("Table: 装配里没有 " + PayloadResourceName + "，负载按空处理");
                        return _table = t;
                    }
                    using (res)
                    using (ZipArchive zip = new ZipArchive(res, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry e in zip.Entries)
                        {
                            string name = e.FullName.Replace('\\', '/');
                            if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                            using (Stream src = e.Open())
                            using (MemoryStream ms = new MemoryStream())
                            {
                                src.CopyTo(ms);
                                t[name] = ms.ToArray();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 资源坏了也不该让安装向导直接崩：报空表，由调用方按「一个都没释放」处理
                    Log.Error("Table 读取 " + PayloadResourceName, ex);
                }
                return _table = t;
            }
        }

        /// <summary>分片落地的目录名，与 Library.DirName 一致</summary>
        private const string ShardDirName = "corpus";

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

            Dictionary<string, byte[]> table = Table();
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

            foreach (KeyValuePair<string, byte[]> kv in table)
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

                    byte[] bytes = kv.Value;

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

            ExtractShards(destDir);
            return n;
        }

        /// <summary>
        /// 把随包的分片 zip 解到 destDir\corpus\，返回写出的文件数。
        ///
        /// 分片刻意不走上面那张 base64 表：负载的每一项都是 C# 字符串字面量，而程序集把
        /// 字面量按 UTF-16 存放，于是 base64 之后再翻一倍 —— 实测放大 2.67×。二十兆的分片
        /// 会胀成五十多兆，还要 csc 去解析一个几十兆的 .cs。走 /resource: 的原始二进制既
        /// 只按 deflate 后的体积算，也不用编译器碰它。
        ///
        /// 装配里没有这个资源时（主程序、或还没带分片的旧安装包）静默返回 0：分片缺席
        /// 不该让整次安装判为失败，主程序本来就能只靠精选集跑。
        /// </summary>
        public static int ExtractShards(string destDir)
        {
            if (string.IsNullOrEmpty(destDir)) return 0;
            int n = 0;
            try
            {
                Stream res = typeof(InstallerPayload).Assembly
                    .GetManifestResourceStream(ShardResourceName);
                if (res == null) return 0;
                using (res)
                using (ZipArchive zip = new ZipArchive(res, ZipArchiveMode.Read))
                {
                    string root = Path.Combine(destDir, ShardDirName);
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        try
                        {
                            string name = e.FullName.Replace('\\', '/');
                            if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                            // 与 ExtractTo 同一条规矩：负载键是构建期生成的，但绝不允许越出目标目录
                            if (name.IndexOf("..", StringComparison.Ordinal) >= 0 || name.StartsWith("/"))
                            {
                                Log.Write("ExtractShards 拒绝越界条目: " + name);
                                continue;
                            }
                            string path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                            string dir = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            using (Stream src = e.Open())
                            using (FileStream dst = new FileStream(path, FileMode.Create, FileAccess.Write))
                                src.CopyTo(dst);
                            n++;
                        }
                        catch (Exception ex) { Log.Error("ExtractShards " + e.FullName, ex); }
                    }
                }
                Log.Write("ExtractShards: 写出 " + n + " 个分片文件（目标 " + Path.Combine(destDir, ShardDirName) + "）");
            }
            catch (Exception ex)
            {
                // 磁盘满、zip 损坏：分片解不出来不该连累已经落地的程序
                Log.Error("ExtractShards 解压失败", ex);
            }
            return n;
        }
    }
}
