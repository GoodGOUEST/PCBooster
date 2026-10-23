// ============================================================
// PC Booster v3.6.8
// Infrastructure/Common/PersistenceManager.cs
// 统一持久化（原子写 + 单写者线程）
// 3.6.7: #7 原子写改 AtomicFile；写任务合并（队列至多一个写动作，
//        do-while 复查 dirty 保证最终一致）
// ============================================================
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
namespace PCBooster.Infrastructure.Common
{
/// <summary>统一持久化管理器（单写者，杜绝并发写）</summary>
public sealed class PersistenceManager<T> where T : class, new()
{
private readonly string _filePath;
private readonly BlockingCollection<Action> _writeQueue = new();
private readonly Thread _writerThread;
private volatile int _queued; // ★ 3.6.7: 写任务合并标志
private T _data = new();
private volatile bool _isDirty;
    public PersistenceManager(string fileName)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCBooster");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, fileName);
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = $"Persist_{fileName}"
        };
        _writerThread.Start();
    }
    public void MarkDirty() => _isDirty = true;
    /// <summary>替换数据并标脏（整体快照保存）</summary>
    public void ReplaceData(T newData)
    {
        if (newData == null) return;
        _data = newData;
        _isDirty = true;
    }
    public T Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return _data;
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<T>(json);
            if (loaded != null) _data = loaded;
        }
        catch (Exception ex) { AppLogger.Error($"加载失败 {_filePath}: {ex.Message}"); }
        return _data;
    }
    /// <summary>★ 3.6.7: 入队写（合并: 队列中至多一个写动作）</summary>
    public bool Save()
    {
        if (!_isDirty) return false;
        TryEnqueueWrite();   // 失败=已有写动作在队/执行中，dirty 复查会兜住
        return true;
    }
    /// <summary>入队一次写盘动作（已有动作时返回 false，不报错）</summary>
    private bool TryEnqueueWrite()
    {
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return false;
        try
        {
            if (_writeQueue.IsAddingCompleted || !_writeQueue.TryAdd(WriteToDisk))
            {
                _queued = 0;
                return false;
            }
            return true;
        }
        catch
        {
            _queued = 0;
            return false;
        }
    }
    /// <summary>退出刷盘（Join 超时 2s）</summary>
    public void FlushAndStop()
    {
        if (_isDirty) TryEnqueueWrite();
        _writeQueue.CompleteAdding();
        _writerThread.Join(Constants.SHUTDOWN_FLUSH_TIMEOUT_MS);
        try { _writeQueue.Dispose(); } catch { }
    }
    private void WriterLoop()
    {
        try { foreach (var action in _writeQueue.GetConsumingEnumerable()) action(); }
        catch { }
    }
    /// <summary>★ 3.6.7: 写后复查 dirty（写入期间被标脏则再写一轮）</summary>
    private void WriteToDisk()
    {
        try
        {
            do
            {
                _isDirty = false;
                var snapshot = _data;              // 引用读取原子: 取一次快照，避免边序列化边被替换
                var json = JsonSerializer.Serialize(snapshot);
                AtomicFile.Write(_filePath, json);
            } while (_isDirty);
        }
        catch (Exception ex) { AppLogger.Error($"写入失败 {_filePath}: {ex.Message}"); }
        finally
        {
            _queued = 0;
            // ★ 3.6.7 修复: 关闭「最后一次标脏」丢失窗口 ——
            //   若在 while 复查之后、_queued 归零之前被标脏，这里补一次入队。
            if (_isDirty) TryEnqueueWrite();
        }
    }
}
}
