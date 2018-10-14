using System;
using Foxpict.Service.Infra;
using Microsoft.EntityFrameworkCore.Storage;
using NLog;
using SimpleInjector;
using SimpleInjector.Lifestyles;

namespace Foxpict.Service.Core {
  /// <summary>
  /// Foxpict用のSimpleInjectionライフサイクル
  /// </summary>
  public class FoxpictAsyncScopedLifestyle {
    public static FoxpictScope BeginScope (Container container) {
      var scope = AsyncScopedLifestyle.BeginScope (container);

      var appdbTransaction = scope.Container.GetInstance<IAppDbContext> ().BeginTransaction ();
      var thumbdbTransaction = scope.Container.GetInstance<IThumbnailDbContext> ().BeginTransaction ();

      return new FoxpictScope (scope, appdbTransaction, thumbdbTransaction);
    }
  }

  public class FoxpictScope : IDisposable {
    private readonly Logger mLogger = LogManager.GetCurrentClassLogger ();

    readonly Scope mScope;

    readonly IDbContextTransaction mAppDbTransaction;

    readonly IDbContextTransaction mThumbDbTransaction;

    bool isComplete;

    internal FoxpictScope (Scope scope, IDbContextTransaction appDbTransaction, IDbContextTransaction thumbDbTransaction) {
      mScope = scope;
      mAppDbTransaction = appDbTransaction;
      mThumbDbTransaction = thumbDbTransaction;
    }

    /// <summary>
    /// スコープ内の処理が正常に完了したことを通知します。
    /// 一度だけ呼び出すことができます。
    /// </summary>
    public void Complete () {
      if (isComplete) throw new ApplicationException ("このスコープはすでに完了しています");

      mScope.Container.GetInstance<IAppDbContext> ().SaveChanges ();
      mScope.Container.GetInstance<IThumbnailDbContext> ().SaveChanges ();

      isComplete = true;
    }

    public void Dispose () {
      // スコープが正常に完了している場合は、スコープ内で発行されたメッセージの処理を行う
      MessagingManager messagingManager = null;
      IMessagingScopeContext scopeMessageContext = null;
      try {
        if (isComplete) {
          mAppDbTransaction.Commit ();
          mThumbDbTransaction.Commit ();
          messagingManager = mScope.Container.GetInstance<MessagingManager> ();
          scopeMessageContext = mScope.Container.GetInstance<IMessagingScopeContext> ();
        } else {
          mAppDbTransaction.Rollback ();
          mThumbDbTransaction.Rollback ();
        }
      } catch (Exception expr) {
        mLogger.Error (expr, "スコープの破棄でエラーが発生しました。");
        mLogger.Debug (expr.StackTrace);

        mAppDbTransaction.Rollback (); // ここで発生したエラーは破棄する
        mThumbDbTransaction.Rollback ();
        messagingManager = null; // 作業変数はクリアし、メッセージの処理は行わない
      } finally {
        mAppDbTransaction.Dispose ();
        mThumbDbTransaction.Dispose ();
        mScope.Dispose ();
      }

      if (messagingManager != null) {
        messagingManager.FireMessaging (scopeMessageContext);
      }
    }
  }
}
