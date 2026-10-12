using System;
using UnityEngine;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 読み込みと表示の予算。上限は実行時に自動では広げない(足りなければ粗い表現のまま見せ、安全速度が下がる)。
    /// </summary>
    [Serializable]
    public sealed class VirtualObjectBudget{
        [Tooltip("同時に準備する表現の数。")]
        [Min(1)] public int MaxConcurrentPrepares=4;
        [Tooltip("1 frame に表示(Activate)する表現の数。")]
        [Min(1)] public int MaxActivationsPerFrame=8;
        [Tooltip("1 frame の表示(Activate)にかける main thread の時間(ms)。")]
        [Min(0.1f)] public float MainThreadMillisecondsPerFrame=2f;
        [Tooltip("準備済み・表示中の表現の GPU の memory の上限(byte)。")]
        public long GpuBytes=512L*1024*1024;
        [Tooltip("準備済み・表示中の表現の CPU の memory の上限(byte)。")]
        public long CpuBytes=512L*1024*1024;
        [Tooltip("細かい表現が見えてから、粗い表現を外すまでの frame 数(重ねてから外す)。")]
        [Min(0)] public int HandoffFrames=2;
        [Tooltip("要求されなくなった準備済みの表現を、memory に余裕があっても解放するまでの frame 数。")]
        [Min(1)] public int IdleReleaseFrames=600;
        [Tooltip("先読みの予測の時間(秒)に掛ける安全係数(準備時間の実測の平滑 × この値)。")]
        [Min(1f)] public float PredictionSafety=2f;
        [Tooltip("1 frame の移動がこれ(m)を超えたら瞬間移動として扱い、予測をやり直す。")]
        [Min(0f)] public float TeleportDistance=1e5f;
        [Tooltip("検証用。準備の完了を、この秒数だけ遅らせる(読み込みの遅い環境の再現)。")]
        [Min(0f)] public float InjectedPrepareDelaySeconds;
    }

    /// <summary>
    /// 1 frame の状態(公開)。<see cref="VirtualObjectSystem.Status"/>。
    /// </summary>
    public struct VirtualObjectStatus{
        /// <summary>
        /// 安全速度(m/s、ルートの局所)。今の読み込みが、望む表現に間に合う接近の速さの上限の推定。遅れていれば今の速さより小さい。
        /// 締切のある要求が無ければ正の無限大。ゲームや camera がこれを使って減速してよい(強制はしない)。
        /// </summary>
        public float SafeSpeed;
        /// <summary>見る camera の今の速さ(m/s)。</summary>
        public float Speed;
        /// <summary>何も見せていない(穴になっている)ルートの実体の数(0 が正常)。</summary>
        public int Uncovered;
        /// <summary>望む帯より粗い表現で見せているノードの数(読み込みの遅れ)。</summary>
        public int Lagging;
        /// <summary>評価したノードの数(展開した子を含む)。</summary>
        public int Nodes;
        /// <summary>準備中、準備済み、表示中の表現の数。</summary>
        public int Preparing;
        public int Ready;
        public int Active;
        /// <summary>この frame の表示(Activate)の数と時間(ms)。</summary>
        public int Activations;
        public float ActivationMilliseconds;
        /// <summary>準備済み・表示中の表現の費用の合計(byte)。</summary>
        public long GpuBytes;
        public long CpuBytes;
        /// <summary>memory が足りず、準備を始められなかった要求の累計。</summary>
        public int StarvedRequests;
        /// <summary>準備の時間の実測の平滑(秒)。</summary>
        public float AveragePrepareSeconds;
        /// <summary>失敗した表現の累計。</summary>
        public int Failures;
    }
}
