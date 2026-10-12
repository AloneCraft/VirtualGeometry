using System;
using UnityEngine;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>表現ごとの設定(表現の factory が型を決める)。<see cref="RepresentationBand.Settings"/> に [SerializeReference] で持つ。</summary>
    public interface IRepresentationSettings{
    }

    /// <summary>
    /// 表現の帯。画面上の大きさ(px。境界の半径を画面へ投影した値)が <see cref="EnterPixels"/> 以上になったら、この表現を望む。
    /// 細かい側から粗い側へ戻るのは <see cref="ExitPixels"/> 未満になったとき(ヒステリシス)。帯は粗い順に並べる(入口の px が単調に増える)。
    /// </summary>
    [Serializable]
    public sealed class RepresentationBand{
        /// <summary>表現の id(<see cref="VirtualObjectRepresentationAttribute"/> の id)。"none" は何も描かない、"children" は子のノードで見せる。</summary>
        public string Representation="none";
        public float EnterPixels;
        public float ExitPixels;
        /// <summary>この帯の表現を、入口の距離のこの倍の距離から先読みする(1 なら先読みしない)。</summary>
        [Min(1f)] public float PrefetchFactor=2f;
        /// <summary>
        /// true なら、ノードを登録した時点で準備し、退避しない(粗い代替として常に見せられるようにする)。
        /// 「なし」より一段細かい、最も粗い表現(ビルボードなど)に付ける。
        /// </summary>
        public bool Resident;
        [SerializeReference] public IRepresentationSettings Settings;

        public RepresentationBand(){
        }

        public RepresentationBand(string representation,float enterPixels,float exitPixels,IRepresentationSettings settings=null){
            this.Representation=representation;this.EnterPixels=enterPixels;this.ExitPixels=exitPixels;this.Settings=settings;
        }

        public bool IsNone=>this.Representation==VirtualObjectRepresentations.None;
        public bool IsChildren=>this.Representation==VirtualObjectRepresentations.Children;
    }

    /// <summary>帯の選び方(ヒステリシス付き)。</summary>
    public static class RepresentationBands{
        /// <summary>
        /// 画面上の大きさ pixels で望む帯の index。previous は前の frame の帯(無ければ −1)。
        /// 細かい方へ進むのは入口の px 以上、粗い方へ戻るのは今の帯の出口の px 未満になったとき。
        /// </summary>
        public static int Select(RepresentationBand[] bands,float pixels,int previous){
            if(bands==null||bands.Length==0){return -1;}
            int index=0;
            for(int i=0;i<bands.Length;i++){
                if(pixels>=bands[i].EnterPixels){index=i;}
            }
            if(previous<0||previous>=bands.Length||index>=previous){return index;}
            // 粗い方へ: 今の帯の出口の px を下回るまで、今の帯に留まる(その間に細かい帯へ戻る判定は入口の px)。
            int result=previous;
            while(result>index&&pixels<bands[result].ExitPixels){result--;}
            return result;
        }

        /// <summary>ヒステリシスなしの帯(先読みの予測に使う)。</summary>
        public static int SelectWithoutHysteresis(RepresentationBand[] bands,float pixels){
            return Select(bands,pixels,-1);
        }

        /// <summary>
        /// 帯の並びの検査(Validator と OnValidate が使う)。粗い順に入口の px が単調に増え、出口が入口以下で、最初の帯の入口が 0。問題が無ければ null。
        /// </summary>
        public static string Validate(RepresentationBand[] bands){
            if(bands==null||bands.Length==0){return "帯が 1 つもありません。";}
            if(bands[0].EnterPixels>0f){return "最初の帯の入口の px は 0 にしてください(画面上の大きさが 0 から覆う)。";}
            for(int i=0;i<bands.Length;i++){
                RepresentationBand band=bands[i];
                if(band==null){return "帯 "+i+" が null です。";}
                if(string.IsNullOrEmpty(band.Representation)){return "帯 "+i+" の表現の id が空です。";}
                if(!(band.EnterPixels>=0f)){return "帯 "+i+" の入口の px が負か NaN です。";}
                if(i>0&&!(band.EnterPixels>bands[i-1].EnterPixels)){return "帯 "+i+" の入口の px("+band.EnterPixels+")が、前の帯("+bands[i-1].EnterPixels+")より大きくありません。";}
                if(i>0&&!(band.ExitPixels<=band.EnterPixels)){return "帯 "+i+" の出口の px("+band.ExitPixels+")が入口("+band.EnterPixels+")より大きいです。";}
                if(i>0&&!(band.ExitPixels>bands[i-1].EnterPixels)){return "帯 "+i+" の出口の px("+band.ExitPixels+")が、前の帯の入口("+bands[i-1].EnterPixels+")以下です(帯が飛ばされます)。";}
                if(!(band.PrefetchFactor>=1f)){return "帯 "+i+" の先読みの倍率は 1 以上にしてください。";}
            }
            return null;
        }
    }
}
