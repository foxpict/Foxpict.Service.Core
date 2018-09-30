using Foxpict.Common.Core;
using Foxpict.Service.Gateway.Repository;
using Foxpict.Service.Infra;
using Hyperion.Pf.Entity;
using NLog;
using SimpleInjector;

namespace Foxpict.Service.Core {
  /// <summary>
  ///
  /// </summary>
  public sealed class MessageContext : IMessageContext {
    private static NLog.Logger LOG = LogManager.GetCurrentClassLogger ();

    readonly Container mContainer;

    readonly string mExtentionName;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    public MessageContext (Container container, object param) {
      this.mContainer = container;
      this.mExtentionName = string.Empty;
      this.Param = param;
    }

    /// <summary>
    /// コンストラクタ(拡張機能向け)
    /// </summary>
    /// <remarks>
    /// 拡張機能へのメッセージコールバックを呼び出す場合は、拡張機能名を設定してインスタンス化します。
    /// </remarks>
    public MessageContext (Container container, string extentionName, object param) {
      this.mContainer = container;
      this.mExtentionName = extentionName;
      this.Param = param;
    }

    /// <summary>
    /// セッション済みオブジェクトを取得します
    /// </summary>
    /// <remarks>
    /// 拡張機能からの呼び出しの場合、リポジトリオブジェクトの取得では拡張機能専用のオブジェクトを返します。
    /// </remarks>
    /// <returns></returns>
    public Type InjectionInstance<Type> ()
    where Type : class {
      var instance = this.mContainer.GetInstance<Type> ();

      if (ObjectUtil.IsInstanceOfGenericType (typeof (PixstockAppRepositoryBase<,>), instance)) {
        var method = instance.GetType ().GetMethod (nameof (PixstockAppRepositoryBase<object, IEntity<long>>.SetCategoryName));
        if (method != null) {
          method.Invoke (instance, new object[] { this.mExtentionName });
        } else {
          LOG.Warn ("リポジトリオブジェクトからメソッドを取得できませんでした。");
        }
      }

      return instance;
    }

    object Param { get; set; }

    public int getInt () {
      if (Param != null) {
        if (Param is int) {
          return (int) Param;
        }
      }
      return 0;
    }

    public long getLong () {
      if (Param != null) {
        if (Param is long) {
          return (long) Param;
        }
      }
      return 0;
    }

    public string getString () {
      if (Param != null) {
        if (Param is string) {
          return Param.ToString ();
        }
      }
      return "";
    }
  }
}
