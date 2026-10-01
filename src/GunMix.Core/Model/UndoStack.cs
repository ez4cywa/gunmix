using System.Text.Json;

namespace GunMix.Core.Model;

/// <summary>
/// 基于整体快照的撤销/重做。文件选择、推子、静音、独听、配方复制、触发编辑都走这里；
/// 扫描缓存、设备切换和试听位置不进入历史。
/// </summary>
public sealed class UndoStack
{
    private const int MaxDepth = 100;
    private readonly JsonSerializerOptions _options;
    private readonly Stack<string> _undo = [];
    private readonly Stack<string> _redo = [];

    public UndoStack(JsonSerializerOptions options)
    {
        _options = options;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? Changed;

    /// <summary>在执行一项编辑前调用：把当前状态压入撤销栈。</summary>
    public void Push(GunProject current)
    {
        _undo.Push(JsonSerializer.Serialize(current, _options));
        if (_undo.Count > MaxDepth) { /* 超限时丢弃最早的记录 */ 
            var arr = _undo.ToArray();
            _undo.Clear();
            for (int i = arr.Length - 2; i >= 0; i--) _undo.Push(arr[i]);
        }
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>撤销：返回上一个状态；current 为当前状态（进入重做栈）。</summary>
    public GunProject? Undo(GunProject current)
    {
        if (_undo.Count == 0) return null;
        _redo.Push(JsonSerializer.Serialize(current, _options));
        var state = _undo.Pop();
        Changed?.Invoke();
        return JsonSerializer.Deserialize<GunProject>(state, _options);
    }

    public GunProject? Redo(GunProject current)
    {
        if (_redo.Count == 0) return null;
        _undo.Push(JsonSerializer.Serialize(current, _options));
        var state = _redo.Pop();
        Changed?.Invoke();
        return JsonSerializer.Deserialize<GunProject>(state, _options);
    }

    public void MarkSaved() { }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
