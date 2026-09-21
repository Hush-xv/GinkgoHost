namespace GinkgoHost.Views;

/// <summary>
/// 窗口连接漏斗正在执行的动作用于跨页面传达文案，此前以 "connect"/"disconnect"/"close"
/// 字符串在三个文件里比较：拼错只会被静默当成默认分支，编译期发现不了。
/// Disconnect 与 Close 都会关总线，区别在于 Close 期间窗口正在拆毁，状态卡要显示「正在关闭」
/// 而不是把界面还原成可再次连接的样子。
/// </summary>
public enum ConnectionAction
{
    Connect,
    Disconnect,
    Close,
}
