﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

/// <summary>
/// 空闲对象容器管理，可实现自动创建、销毁、扩张收缩，解决【实例】长时间占用问题
/// </summary>
public partial class IdleBus<T> : IDisposable where T : class, IDisposable
{

    ConcurrentDictionary<string, ItemInfo> _dic;
    ConcurrentDictionary<string, ItemInfo> _removePending;
    object _usageLock = new object();
    int _usageQuantity;
    TimeSpan _defaultIdle;

    /// <summary>
    /// 按空闲时间1分钟，创建空闲容器
    /// </summary>
    public IdleBus() : this(TimeSpan.FromMinutes(1)) {}

    /// <summary>
    /// 指定空闲时间、创建空闲容器
    /// </summary>
    /// <param name="idle">空闲时间</param>
    public IdleBus(TimeSpan idle)
    {
        _dic = new ConcurrentDictionary<string, ItemInfo>();
        _removePending = new ConcurrentDictionary<string, ItemInfo>();
        _usageQuantity = 0;
        _defaultIdle = idle;
    }

    /// <summary>
    /// 根据 key 获得或创建【实例】（线程安全）<para></para>
    /// key 未注册时，抛出异常
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public T Get(string key) => InternalGet(key, true);
    /// <summary>
    /// 根据 key 获得或创建【实例】（线程安全）<para></para>
    /// key 未注册时，返回 null
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public T TryGet(string key) => InternalGet(key, false);

    T InternalGet(string key, bool isThrow)
    {
        if (isdisposed && isThrow) throw new Exception($"{key} 实例获取失败 ，{nameof(IdleBus<T>)} 对象已释放");
        if (_dic.TryGetValue(key, out var item) == false)
        {
            if (isThrow)
            {
                var error = new Exception($"{key} 实例获取失败，因为没有注册");
                this.OnNotice(new NoticeEventArgs(NoticeType.Get, key, error, error.Message));
                throw error;
            }
            return null;
        }

        var now = DateTime.Now;
        var ret = item.GetOrCreate();
        var tsms = DateTime.Now.Subtract(now).TotalMilliseconds;
        this.OnNotice(new NoticeEventArgs(NoticeType.Get, key, null, $"{key} 实例获取成功 {item.activeCounter}次{(tsms > 5 ? $"，耗时 {tsms}ms" : "")}"));
        //this.ThreadLiveWatch(item); //这种模式采用 Sorted 性能会比较慢
        this.ThreadScanWatch(item); //这种在后台扫描 _dic ，定时要求可能没那么及时
        return ret;
    }

    /// <summary>
    /// 注册【实例】
    /// </summary>
    /// <param name="key"></param>
    /// <param name="create">实例创建方法</param>
    /// <returns></returns>
    public IdleBus<T> Register(string key, Func<T> create)
    {
        InternalRegister(key, create, null, true);
        return this;
    }
    public IdleBus<T> Register(string key, Func<T> create, TimeSpan idle)
    {
        InternalRegister(key, create, idle, true);
        return this;
    }
    public bool TryRegister(string key, Func<T> create) => InternalRegister(key, create, null, false);
    public bool TryRegister(string key, Func<T> create, TimeSpan idle) => InternalRegister(key, create, idle, false);

    //public void Remove(string key) => InternalRemove(key, true);
    public bool TryRemove(string key) => InternalRemove(key, false);

    /// <summary>
    /// 已创建【实例】数量
    /// </summary>
    public int UsageQuantity => _usageQuantity;
    /// <summary>
    /// 注册数量
    /// </summary>
    public int Quantity => _dic.Count;
    /// <summary>
    /// 通知事件
    /// </summary>
    public event EventHandler<NoticeEventArgs> Notice;

    bool InternalRegister(string key, Func<T> create, TimeSpan? idle, bool isThrow)
    {
        if (isdisposed) throw new Exception($"{key} 注册失败 ，{nameof(IdleBus<T>)} 对象已释放");
        var error = new Exception($"{key} 注册失败，请勿重复注册");
        if (_dic.ContainsKey(key))
        {
            this.OnNotice(new NoticeEventArgs(NoticeType.Register, key, error, error.Message));
            if (isThrow) throw error;
            return false;
        }

        if (idle == null) idle = _defaultIdle;
        //if (idle < TimeSpan.FromSeconds(5))
        //{
        //    var limitError = new Exception($"{key} 注册失败，{nameof(idle)} 参数必须 >= 5秒");
        //    this.OnNotice(new NoticeEventArgs(NoticeType.Register, key, limitError, limitError.Message));
        //    if (isThrow) throw error;
        //    return this;
        //}
        var added = _dic.TryAdd(key, new ItemInfo
        {
            ib = this,
            key = key,
            create = create,
            idle = idle.Value,
        });
        if (added == false)
        {
            this.OnNotice(new NoticeEventArgs(NoticeType.Register, key, error, error.Message));
            if (isThrow) throw error;
            return false;
        }
        this.OnNotice(new NoticeEventArgs(NoticeType.Register, key, null, $"{key} 注册成功，{_usageQuantity}/{Quantity}"));
        return true;
    }
    bool InternalRemove(string key, bool isThrow)
    {
        if (isdisposed) throw new Exception($"{key} 删除失败 ，{nameof(IdleBus<T>)} 对象已释放");
        if (_dic.TryRemove(key, out var item) == false)
        {
            var error = new Exception($"{key} 删除失败 ，因为没有注册");
            this.OnNotice(new NoticeEventArgs(NoticeType.Remove, key, error, error.Message));
            if (isThrow) throw error;
            return false;
        }

        Interlocked.Exchange(ref item.releaseErrorCounter, 0);
        item.lastActiveTime = DateTime.Now;
        if (item.value == null) item.lastActiveTime = DateTime.Now.Subtract(item.idle).AddSeconds(-60); //延时删除
        _removePending.TryAdd(Guid.NewGuid().ToString(), item);
        this.OnNotice(new NoticeEventArgs(NoticeType.Remove, item.key, null, $"{key} 删除成功，并且已标记为延时释放，{_usageQuantity}/{Quantity}"));
        return true;
    }
    void InternalRemoveDelayHandler()
    {
        //处理延时删除
        var removeKeys = _removePending.Keys;
        foreach (var removeKey in removeKeys)
        {
            if (_removePending.TryGetValue(removeKey, out var removeItem) == false) continue;
            if (DateTime.Now.Subtract(removeItem.lastActiveTime) <= removeItem.idle) continue;
            try
            {
                removeItem.Release(() => true);
            }
            catch (Exception ex)
            {
                var tmp1 = Interlocked.Increment(ref removeItem.releaseErrorCounter);
                this.OnNotice(new NoticeEventArgs(NoticeType.Remove, removeItem.key, ex, $"{removeKey} ---延时释放执行出错({tmp1}次)：{ex.Message}"));
                if (tmp1 < 3)
                    continue;
            }
            removeItem.Dispose();
            _removePending.TryRemove(removeKey, out var oldItem);
        }
    }

    void OnNotice(NoticeEventArgs e)
    {
        if (this.Notice != null) this.Notice(this, e);
        else Trace.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss")}] 线程{Thread.CurrentThread.ManagedThreadId}：{e.Log}");
    }

    #region Dispose
    ~IdleBus() => Dispose();
    bool isdisposed = false;
    object isdisposedLock = new object();
    public void Dispose()
    {
        if (isdisposed) return;
        lock (isdisposedLock)
        {
            if (isdisposed) return;
            isdisposed = true;
        }
        foreach (var item in _removePending.Values) item.Dispose();
        foreach (var item in _dic.Values) item.Dispose();

        _removePending.Clear();
        _dic.Clear();
        _usageQuantity = 0;
    }
    #endregion
}