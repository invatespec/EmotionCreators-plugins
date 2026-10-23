using System;

namespace EC_SceneAudit.Repair
{
    // 路径值必须保持单行身份；TextArea 仅用于视觉换行，进入配置或文件 API 前先清洗 CR/LF。
    internal static class RepairPathText
    {
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? string.Empty;
            return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }
}
