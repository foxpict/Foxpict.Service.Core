using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Foxpict.Service.Infra;
using Foxpict.Service.Infra.Extention;
using Hyperion.Pf.Entity;
using NLog;
using SimpleInjector;
using SimpleInjector.Lifestyles;

namespace Foxpict.Service.Core {
  /// <summary>
  /// メッセージングフレームワーク
  /// </summary>
  public class MessagingManager : IMessagingManager {
    private static NLog.Logger LOG = LogManager.GetCurrentClassLogger ();

    private readonly ConcurrentDictionary<string, LinkedList<MessageQueueItem>> mSubscribeList = new ConcurrentDictionary<string, LinkedList<MessageQueueItem>> ();

    public readonly Container mContainer;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="container"></param>
    public MessagingManager (Container container) {
      this.mContainer = container;
    }

    /// <summary>
    /// メッセージの呼び出しに対応するコールバックを登録する
    /// </summary>
    /// <param name="messageName">メッセージ名</param>
    /// <param name="callback">メッセージが実行される際に呼び出すコールバック関数（重複不可）</param>
    public void RegisterMessage (string messageName, MessageCallback callback) {
      _RegisterMessage (messageName, null, callback);
    }

    /// <summary>
    /// メッセージの呼び出しに対応するコールバックを登録する（拡張機能向け）
    /// </summary>
    public void RegisterMessage (string messageName, IExtentionMetaInfo extention, MessageCallback callback) {
      _RegisterMessage (messageName, extention, callback);
    }

    /// <summary>
    /// 登録したコールバックを解除する
    /// </summary>
    /// <param name="messageName">メッセージ名</param>
    /// <param name="callback">解除するコールバック関数</param>
    public void UnegisterMessage (string messageName, MessageCallback callback) {
      if (!mSubscribeList.ContainsKey (messageName))
        return;
      LinkedList<MessageQueueItem> queue;
      if (mSubscribeList.TryGetValue (messageName, out queue)) {
        var r = from u in queue
        where u.ExtentionName == ""
        select u;
        foreach (var queueItem in r.ToArray ()) {
          if (queueItem.callback == callback)
            queue.Remove (queueItem);
        }
      }
    }

    /// <summary>
    /// 登録したコールバックを解除する
    /// </summary>
    /// <param name="messageName">メッセージ名</param>
    /// <param name="extention"></param>
    /// <param name="callback">解除するコールバック関数</param>
    public void UnegisterMessage (string messageName, IExtentionMetaInfo extention, MessageCallback callback) {
      if (!mSubscribeList.ContainsKey (messageName))
        return;
      LinkedList<MessageQueueItem> queue;
      if (mSubscribeList.TryGetValue (messageName, out queue)) {
        var r = from u in queue
        where u.ExtentionName == extention.Name
        select u;
        foreach (var queueItem in r.ToArray ()) {
          if (queueItem.callback == callback)
            queue.Remove (queueItem);
        }
      }
    }

    /// <summary>
    /// メッセージを実行します。
    /// メッセージは新しいスコープで実行されます。
    /// </summary>
    /// <param name="messagingContext">メッセージを格納したコンテキスト</param>
    public void FireMessaging (IMessagingScopeContext messagingContext) {
      var currentMessagingContext = (MessagingScopeContext) messagingContext;

      while (!currentMessagingContext.mDispatcherList.IsEmpty) {
        DispatcherItem item;
        if (currentMessagingContext.mDispatcherList.TryDequeue (out item)) {
          string messageName = item.EventName;
          object param = item.Param;
          using (var scope = FoxpictAsyncScopedLifestyle.BeginScope (mContainer)) {
            try {
              LinkedList<MessageQueueItem> queue;
              if (mSubscribeList.TryGetValue (messageName, out queue)) {
                var queueArray = queue.ToArray ();
                foreach (var queueItem in queueArray) {

                  if (string.IsNullOrEmpty (queueItem.ExtentionName)) {
                    var messagecontext = new MessageContext (mContainer, param);
                    queueItem.callback (messagecontext);
                  } else {
                    // 拡張機能に対するメッセージのディスパッチ
                    var messagecontext = new MessageContext (mContainer, queueItem.ExtentionName, param);
                    queueItem.callback (messagecontext);
                  }
                }
              }
              scope.Complete ();
            } catch (Exception expr) {
              LOG.Error (expr, "拡張機能実行中に処理が停止しました。");
              LOG.Error (expr.StackTrace);
            }
          }
        }
      }

    }

    private void _RegisterMessage (string messageName, IExtentionMetaInfo extention, MessageCallback callback) {
      if (!mSubscribeList.ContainsKey (messageName))
        mSubscribeList.TryAdd (messageName, new LinkedList<MessageQueueItem> ());

      LinkedList<MessageQueueItem> queue;
      if (mSubscribeList.TryGetValue (messageName, out queue)) {
        string extentionName = "";
        if (extention != null) {
          extentionName = extention.Name;
        }

        var r = from u in queue
        where u.ExtentionName == extentionName && u.callback == callback
        select u;

        if (r.Count () == 0)
          queue.AddLast (new MessageQueueItem {
            callback = callback,
              ExtentionName = extentionName
          });
      }
    }

    public class DispatcherItem {
      public string EventName;

      public object Param;
    }

    struct MessageQueueItem {
      public MessageCallback callback;

      /// <summary>
      /// 拡張機能へのコールバックの場合のみ、拡張機能名称を設定する
      /// </summary>
      public string ExtentionName;
    }
  }
}
