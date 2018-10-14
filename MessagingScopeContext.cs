using System.Collections.Concurrent;
using static Foxpict.Service.Core.MessagingManager;

namespace Foxpict.Service.Core {
  public interface IMessagingScopeContext {
    /// <summary>
    /// メッセージを登録します
    /// </summary>
    /// <param name="messageName"></param>
    /// <param name="param"></param>
    void Dispatcher (string messageName, int param);

    /// <summary>
    /// メッセージを登録します
    /// </summary>
    /// <param name="messageName"></param>
    /// <param name="param"></param>
    void Dispatcher (string messageName, long param);

    /// <summary>
    /// メッセージを登録します
    /// </summary>
    /// <param name="messageName"></param>
    /// <param name="param"></param>
    void Dispatcher (string messageName, string param);
  }

  public class MessagingScopeContext : IMessagingScopeContext {
    internal readonly ConcurrentQueue<DispatcherItem> mDispatcherList = new ConcurrentQueue<DispatcherItem> ();

    public void Dispatcher (string messageName, int param) {
      this._Dispatcher (messageName, (object) param);
    }

    /// <summary>
    /// メッセージの処理を呼び出す
    /// </summary>
    /// <param name="messageName"></param>
    /// <param name="param"></param>
    public void Dispatcher (string messageName, long param) {
      this._Dispatcher (messageName, (object) param);
    }

    /// <summary>
    /// メッセージの処理を呼び出す
    /// </summary>
    /// <param name="messageName"></param>
    /// <param name="param"></param>
    public void Dispatcher (string messageName, string param) {
      this._Dispatcher (messageName, (object) param);
    }

    private void _Dispatcher (string messageName, object param) {
      mDispatcherList.Enqueue (new DispatcherItem { EventName = messageName, Param = param });
    }
  }
}
