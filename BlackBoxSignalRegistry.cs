using System;
using System.Collections.Generic;
using System.Linq;

namespace Alife.BlackBoxSuite;

/// <summary>
/// 外部体征信号注册表。允许其他插件向黑盒注册自定义信号，
/// 注册后可直接用于自定义触发规则的条件里。
/// </summary>
public static class BlackBoxSignalRegistry
{
    /// <summary>信号类型</summary>
    public enum SignalKind { Number, Text }

    /// <summary>一条外部信号定义</summary>
    public sealed class Entry
    {
        public string Name = "";
        public SignalKind Kind = SignalKind.Number;
        public Func<double> NumberGetter;
        public Func<string> TextGetter;
        public string Description = "";
        public string Owner = "";
    }

    private static readonly Dictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _lock = new();

    /// <summary>注册一个数值型信号</summary>
    public static bool RegisterNumber(string name, Func<double> getter, string description, string owner = "")
    {
        return Register(name, SignalKind.Number, getter, null, description, owner);
    }

    /// <summary>注册一个文本型信号</summary>
    public static bool RegisterText(string name, Func<string> getter, string description, string owner = "")
    {
        return Register(name, SignalKind.Text, null, getter, description, owner);
    }

    private static bool Register(string name, SignalKind kind,
        Func<double> numberGetter, Func<string> textGetter,
        string description, string owner)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string key = name.Trim();
        lock (_lock)
        {
            _entries[key] = new Entry
            {
                Name = key,
                Kind = kind,
                NumberGetter = numberGetter,
                TextGetter = textGetter,
                Description = description ?? "",
                Owner = owner ?? ""
            };
        }
        return true;
    }

    /// <summary>注销一个信号</summary>
    public static bool Unregister(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock) return _entries.Remove(name.Trim());
    }

    /// <summary>按来源清空该来源注册的全部信号，插件卸载时调用</summary>
    public static int ClearOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return 0;
        lock (_lock)
        {
            var keys = _entries
                .Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            foreach (var k in keys) _entries.Remove(k);
            return keys.Count;
        }
    }

    /// <summary>查找一个外部信号</summary>
    public static bool TryGet(string name, out Entry entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock) return _entries.TryGetValue(name.Trim(), out entry);
    }

    /// <summary>列出全部已注册的外部信号</summary>
    public static List<Entry> All()
    {
        lock (_lock) return _entries.Values.ToList();
    }
}
