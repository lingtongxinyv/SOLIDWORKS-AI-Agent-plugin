using System;
using System.Linq;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAiAssistant.Cad.Documents
{
    /// <summary>有打开零件时的用户选择。</summary>
    public enum DocChoice
    {
        /// <summary>在当前活动零件上继续。</summary>
        ContinueCurrent,
        /// <summary>放弃当前文档上下文，新建零件。</summary>
        CreateNew
    }

    /// <summary>当前文档状态快照（供 UI 决定是否需要弹"继续/新建"）。</summary>
    public sealed class DocState
    {
        public bool HasActiveDocument { get; set; }
        public bool ActiveIsPart { get; set; }
        public string ActiveDocTitle { get; set; } = "";
        public int OpenPartCount { get; set; }
    }

    /// <summary>
    /// 文档策略：建模命令的目标文档确定。
    /// 规则（spec FR-11）：无打开零件 → 自动新建；有打开零件 → 由调用方（UI/测试台）决定继续或新建。
    /// 内部统一单位：米（SolidWorks 内部单位即米，不做换算层）。
    /// </summary>
    public sealed class DocService
    {
        private readonly SwSession _session;

        public DocService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>当前文档状态（任何线程可调，内部封送 STA）。</summary>
        public DocState GetState()
        {
            return _session.OnSta(() =>
            {
                var state = new DocState();
                var active = _session.GetActiveDocument();
                state.HasActiveDocument = active != null;
                if (active != null)
                {
                    state.ActiveIsPart = IsPart(active);
                    state.ActiveDocTitle = SafeTitle(active);
                }
                state.OpenPartCount = _session.GetDocuments().Count(IsPart);
                return state;
            });
        }

        /// <summary>新建零件文档（任何线程可调）。</summary>
        public IModelDoc2 NewPart()
        {
            return _session.NewPartDocument();
        }

        /// <summary>当前活动零件（无活动文档或非零件时返回 null）。只读取，绝不新建。</summary>
        public IModelDoc2 GetActivePart()
        {
            return _session.OnSta(() =>
            {
                var active = _session.GetActiveDocument();
                return active != null && IsPart(active) ? active : null;
            });
        }

        /// <summary>当前活动工程图（无活动文档或非工程图时返回 null）。只读取。</summary>
        public IModelDoc2 GetActiveDrawing()
        {
            return _session.OnSta(() =>
            {
                var active = _session.GetActiveDocument();
                if (active == null) return null;
                try
                {
                    return active.GetType() == (int)swDocumentTypes_e.swDocDRAWING ? active : null;
                }
                catch { return null; }
            });
        }

        /// <summary>
        /// 确保有可用于建模的零件文档：
        /// 无活动文档 → 自动新建；活动文档为零件 → askUser 决定继续/新建；
        /// 活动文档为装配/工程图 → 自动新建零件并记录日志。
        /// askUser 为 null 时默认「在当前零件继续」。返回值可能为 null（用户取消由 UI 层控制，不经此路径）。
        /// </summary>
        public IModelDoc2 EnsurePartDocument(Func<DocState, DocChoice> askUser = null)
        {
            return _session.OnSta(() =>
            {
                var state = GetState();

                if (!state.HasActiveDocument)
                {
                    Log.Info("Cad", "文档策略：无打开文档，自动新建零件");
                    return _session.NewPartDocument();
                }

                if (!state.ActiveIsPart)
                {
                    Log.Info("Cad", $"文档策略：活动文档「{state.ActiveDocTitle}」不是零件，自动新建零件");
                    return _session.NewPartDocument();
                }

                var choice = askUser?.Invoke(state) ?? DocChoice.ContinueCurrent;
                if (choice == DocChoice.CreateNew)
                {
                    Log.Info("Cad", $"文档策略：用户选择在「{state.ActiveDocTitle}」之外新建零件");
                    return _session.NewPartDocument();
                }

                Log.Info("Cad", $"文档策略：在当前零件「{state.ActiveDocTitle}」继续");
                return _session.GetActiveDocument();
            });
        }

        private static bool IsPart(IModelDoc2 doc)
        {
            try { return doc.GetType() == (int)swDocumentTypes_e.swDocPART; }
            catch { return false; }
        }

        /// <summary>
        /// 文档标题（不抛异常，读取失败返回 "&lt;未知&gt;"）。分层纪律：Planner 不得直接调 doc.GetTitle()。
        /// </summary>
        public string GetTitle(IModelDoc2 doc)
        {
            if (doc == null) return "<未知>";
            return _session.OnSta(() => SafeTitle(doc));
        }

        /// <summary>
        /// 强制重建（topOnly=false 全重建）。分层纪律：Planner 不得直接调 ForceRebuild3。
        /// </summary>
        public void ForceRebuild(IModelDoc2 doc, bool topOnly = false)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _session.OnSta<object>(() =>
            {
                try
                {
                    doc.ForceRebuild3(topOnly);
                    return null;
                }
                catch (Exception ex) { throw CadException.FromCom("强制重建", ex); }
            });
        }

        private static string SafeTitle(IModelDoc2 doc)
        {
            try { return doc.GetTitle(); } catch { return "<未知>"; }
        }
    }
}
