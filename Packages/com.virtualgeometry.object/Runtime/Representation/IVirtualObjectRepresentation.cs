using System;
using System.Threading;
using UnityEngine;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>表現の状態。Missing → Preparing → Ready → Active(見せている)→ Ready(隠した。資源は残る)→ Released。</summary>
    public enum RepresentationState{
        Missing,
        Preparing,
        Ready,
        Active,
        Released,
        Failed,
    }

    /// <summary>表現の費用の見積もり(予算と安全速度に使う)。</summary>
    [Serializable]
    public struct RepresentationCost{
        /// <summary>GPU の memory(byte)。</summary>
        public long GpuBytes;
        /// <summary>CPU の memory(byte)。</summary>
        public long CpuBytes;
        /// <summary>準備にかかる時間の見積もり(秒。実測が無いときに使う)。</summary>
        public float PrepareSeconds;
        /// <summary>表示(main thread)にかかる時間の見積もり(ミリ秒)。</summary>
        public float ActivateMilliseconds;

        public RepresentationCost(long gpuBytes,long cpuBytes,float prepareSeconds,float activateMilliseconds){
            this.GpuBytes=gpuBytes;this.CpuBytes=cpuBytes;this.PrepareSeconds=prepareSeconds;this.ActivateMilliseconds=activateMilliseconds;
        }
    }

    /// <summary>表現を作るときに渡す値。</summary>
    public readonly struct RepresentationRequest{
        public readonly VirtualObjectNode Node;
        public readonly int BandIndex;
        public readonly RepresentationBand Band;

        public RepresentationRequest(VirtualObjectNode node,int bandIndex,RepresentationBand band){
            this.Node=node;this.BandIndex=bandIndex;this.Band=band;
        }
    }

    /// <summary>
    /// 表現(ノードを見せる 1 つの方法)の契約。人や AI が新しい種類を作るときは <see cref="VirtualObjectRepresentationBase"/> を継承する。
    /// 呼び出しはすべて main thread から。BeginPrepare は重い処理を非同期に始めてよい(完了は Poll で知らせる)。
    /// </summary>
    public interface IVirtualObjectRepresentation{
        RepresentationState State{get;}
        RepresentationCost EstimateCost();
        /// <summary>準備を始める(資源の読み込み、生成など)。token が取り消されたら、資源を残さずに止める。</summary>
        void BeginPrepare(VirtualObjectContext context,CancellationToken token);
        /// <summary>毎 frame 呼ぶ。準備が終わっていれば true(State は Ready)。</summary>
        bool Poll(VirtualObjectContext context);
        /// <summary>見せる。Ready のときだけ呼ばれる。</summary>
        void Activate(VirtualObjectContext context);
        /// <summary>実際に描かれる状態になったか(粗い表現を外す条件)。</summary>
        bool IsVisible{get;}
        /// <summary>見せている間、毎 frame 呼ぶ(配置の更新、camera に向けるなど)。</summary>
        void UpdatePose(VirtualObjectContext context);
        /// <summary>隠す。資源は残す(再び Activate できる)。</summary>
        void Deactivate(VirtualObjectContext context);
        /// <summary>資源を解放する。何度呼んでもよい。</summary>
        void Release(VirtualObjectContext context);
    }

    /// <summary>表現の factory。<see cref="VirtualObjectRepresentationAttribute"/> を付けたクラスを、id で作る。</summary>
    public interface IVirtualObjectRepresentationFactory{
        IVirtualObjectRepresentation Create(in RepresentationRequest request);
    }

    /// <summary>
    /// 表現の factory に付ける属性。id は <see cref="RepresentationBand.Representation"/> に書く値、SettingsType は帯の設定の型。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class,AllowMultiple=false,Inherited=false)]
    public sealed class VirtualObjectRepresentationAttribute:Attribute{
        public string Id{get;}
        public Type SettingsType{get;}

        public VirtualObjectRepresentationAttribute(string id,Type settingsType=null){
            this.Id=id;this.SettingsType=settingsType;
        }
    }
}
