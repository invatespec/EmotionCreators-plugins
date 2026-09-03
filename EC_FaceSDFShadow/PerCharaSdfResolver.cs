using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ExtensibleSaveFormat;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 逐角色手工 SDF 来源目录：由 EC_Profile 角色描述里的 <c>@FaceSDFShadow:[目录名]</c> 指令指定，
    /// 解析到 <c>DataDir/SDF/perChara/&lt;目录名&gt;/</c>；无指令或指令无效则回全局 <c>DataDir/SDF/</c>。
    ///
    /// 不引用 EC_Profile 程序集，只经 ESF 读它写在角色卡上的扩展数据，
    /// 因此 Profile 未安装时自然读不到文本、静默回全局。
    ///
    /// 不能按角色名建目录：角色名会重名；也不能把路径做成材质属性让用户在 ME 里填
    /// ——ME 的 ShaderPropertyType 没有 String。
    /// </summary>
    internal static class PerCharaSdfResolver
    {
        private const string ProfileDataId = "KK_Profile";
        private const string ProfileTextKey = "ProfileText";
        internal const string Marker = "@FaceSDFShadow";

        /// 定界符收半角 []、全角［］、中文【】：中文输入法默认打全角，不收必然被判成格式错误。
        /// 目录名不跨行（[^...\r\n]）——用户漏写右定界符时只吞当前行，不会把整篇描述当目录名。
        /// 尖括号定界不可用：EC_Profile 描述框开了 richText，&lt;&gt; 会被 TMP 当标签吃掉。
        private static readonly Regex DirectiveRegex = new Regex(
            @"@FaceSDFShadow\s*[:：]\s*[\[［【]([^\]］】\r\n]+)[\]］】]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        /// Poll 每 15 帧对每个角色调一次，同一问题会刷屏；按问题去重只报第一次。
        private static readonly HashSet<string> Warned = new HashSet<string>();

        /// <summary>
        /// 返回该角色的手工 SDF 目录绝对路径；无指令/指令无效/目录不存在时返回 null（调用方回全局）。
        /// </summary>
        internal static string Resolve(ChaInfo cha)
        {
            string profileText = ReadProfileText(cha);
            string dirName = ParseDirectiveName(profileText);
            if (dirName == null)
            {
                // 无指令是绝大多数角色的正常情况，但"写了却不生效"和"根本没写"在结果上无法区分，
                // 全静默会让排查无从下手。按角色报一次，说明卡上到底读到了什么。
                ReportNoDirective(cha, profileText);
                return null;
            }

            string dir = Path.Combine(FaceSDFShadowPlugin.DataDir, "SDF", "perChara", dirName);
            if (Directory.Exists(dir))
            {
                NoteOnce("resolved:" + dirName, $"Using per-character SDF directory: {dir}");
                return dir;
            }

            WarnOnce("missing:" + dirName,
                $"Directive '{Marker}:[{dirName}]' points to a missing directory " +
                $"(expected SDF/perChara/{dirName}/); falling back to global SDF/.");
            return null;
        }

        /// <summary>
        /// 指令未生效时给出可区分的原因：卡上没有 Profile 数据 / 有文本但没写指令。
        /// 指令写了但格式错、目录名非法、目录不存在，各自已有专门的 warning。
        /// </summary>
        private static void ReportNoDirective(ChaInfo cha, string profileText)
        {
            string who = cha?.fileParam?.fullname ?? "(unknown)";
            if (profileText == null)
            {
                NoteOnce("noprofile:" + who,
                    $"No {ProfileDataId} data on '{who}' (EC_Profile not installed, or the card " +
                    "was saved without a profile text); using global SDF/.");
            }
            else if (profileText.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) < 0)
            {
                NoteOnce("nodirective:" + who,
                    $"Profile text on '{who}' ({profileText.Length} chars) has no {Marker} " +
                    "directive; using global SDF/.");
            }
        }

        /// <summary>
        /// 从 ESF 取 EC_Profile 写在角色卡上的描述文本。Profile 未装 → 无该 ID → null。
        /// </summary>
        private static string ReadProfileText(ChaInfo cha)
        {
            var chaFile = cha?.chaFile;
            if (chaFile == null) return null;

            PluginData data;
            try
            {
                data = ExtendedSave.GetExtendedDataById(chaFile, ProfileDataId);
            }
            catch (Exception ex)
            {
                WarnOnce("esf", $"Failed to read {ProfileDataId} extended data: {ex.Message}");
                return null;
            }

            if (data?.data == null) return null;
            // Profile 存的是 string，但 MessagePack 反序列化类型不保证，沿用 Profile 自己的 ToString 兜底
            return data.data.TryGetValue(ProfileTextKey, out var raw) ? raw?.ToString() : null;
        }

        /// <summary>
        /// 从描述文本里取出目录名。多条指令只认第一条。
        /// </summary>
        private static string ParseDirectiveName(string profileText)
        {
            if (string.IsNullOrEmpty(profileText)) return null;

            // 绝大多数描述不含指令；先做廉价子串检查，避免每轮轮询对长文本跑正则
            int at = profileText.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            var match = DirectiveRegex.Match(profileText, at);
            if (!match.Success)
            {
                // 写了指令名却没成对定界符：静默回全局会让用户以为插件坏了，必须报
                string snippet = profileText.Substring(at, Math.Min(60, profileText.Length - at));
                WarnOnce("syntax:" + snippet,
                    $"Malformed directive near \"{snippet}\"; expected {Marker}:[dirName]. " +
                    "Falling back to global SDF/.");
                return null;
            }

            string name = match.Groups[1].Value.Trim();
            return IsValidDirName(name) ? name : null;
        }

        /// <summary>
        /// 目录名必须是 perChara/ 下的合法单层名：挡掉 .. 与路径分隔符，避免指令写出目录外的路径。
        /// </summary>
        private static bool IsValidDirName(string name)
        {
            string reason = null;
            if (string.IsNullOrEmpty(name))
                reason = "empty";
            else if (name == "." || name == "..")
                reason = "path traversal";
            else if (name.IndexOfAny(InvalidNameChars) >= 0)
                reason = "contains a path separator or an invalid filename character";

            if (reason == null) return true;

            WarnOnce("invalid:" + name,
                $"Directive directory name \"{name}\" rejected ({reason}); " +
                "it must be a single folder name under SDF/perChara/. Falling back to global SDF/.");
            return false;
        }

        private static void WarnOnce(string key, string message)
        {
            if (!Warned.Add(key)) return;
            FaceSDFShadowPlugin.Log.LogWarning($"[PerCharaSDF] {message}");
        }

        /// 非错误的一次性说明（正常回退全局也要能看见），与 WarnOnce 共用去重集合。
        private static void NoteOnce(string key, string message)
        {
            if (!Warned.Add(key)) return;
            FaceSDFShadowPlugin.Log.LogInfo($"[PerCharaSDF] {message}");
        }
    }
}
