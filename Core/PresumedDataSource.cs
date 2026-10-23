// ============================================================
// PC Booster v3.6.8
// Core/PresumedDataSource.cs
// 缺位推定数据源（三级回退 + 置信度）
// ============================================================

namespace PCBooster.Core
{
    /// <summary>缺位推定数据源（泛型三级回退）</summary>
    public class PresumedDataSource<T>
    {
        private readonly Func<T> _primary;
        private readonly Func<T> _secondary;
        private readonly Func<T, bool> _isValid;
        private readonly T _fallback;
        private readonly object _lock = new();
        private T _lastGood;
        private bool _hasLastGood;

        public double Confidence { get; private set; } = 1.0;

        public PresumedDataSource(Func<T> primary, Func<T> secondary, T fallback,
            Func<T, bool> isValid = null)
        { _primary = primary; _secondary = secondary; _fallback = fallback; _isValid = isValid; }

        /// <summary>三级回退（1.0/0.6/0.4/0.1 置信度）</summary>
        public T Get()
        {
            lock (_lock)   // ★ 3.6.8: 快/慢心跳可能并发调用同一实例
            {
                try
                {
                    var v = _primary();
                    _lastGood = v; _hasLastGood = true; Confidence = 1.0;
                    return v;
                }
                catch { }

                // ★ 3.6.8: 上次好值也要过有效性校验（进程可能已退出，PID 可能被复用）
                if (_hasLastGood && (_isValid == null || _isValid(_lastGood)))
                { Confidence = 0.6; return _lastGood; }

                try
                {
                    var v = _secondary();
                    // ★ 3.6.8: 次级源返回 fallback（0 = 没有前台进程）说明“无数据”，
                    //   不能算 0.4；否则降级引擎的 Minimal 等级永远不会触发。
                    Confidence = EqualityComparer<T>.Default.Equals(v, _fallback) ? 0.1 : 0.4;
                    return v;
                }
                catch { }

                Confidence = 0.1;
                return _fallback;
            }
        }
    }
}