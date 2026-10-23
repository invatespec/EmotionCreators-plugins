using System;

namespace EC_SceneAudit.Repair
{
    // 修复清单和 F8 资料包的输入错误；消息直接面向使用者，UI 不再改写或推测。
    internal sealed class RepairInputException : Exception
    {
        public RepairInputException(string message)
            : base(message)
        {
        }

        public RepairInputException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
