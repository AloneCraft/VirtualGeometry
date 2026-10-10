using System;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;

namespace VirtualGeometry.World{
    /// <summary>手続き生成の惑星の高さの形。振幅は Hmax(<see cref="PlanetTerrainSource.HeightAmplitude"/>)に対する割合。</summary>
    [Serializable]
    public struct PlanetHeightSettings{
        /// <summary>大陸の値(およそ −1〜1)がこれより上なら陸。</summary>
        public float SeaLevel;
        /// <summary>海岸の幅の半分(大陸の値の単位)。</summary>
        public float ShoreWidth;
        public float OceanDepth;
        public float PlainsHeight;
        /// <summary>山(ridged noise)の最初の octave と、その振幅。山は 6 octave で、1 octave ごとに振幅が半分になる。</summary>
        public int MountainOctave;
        public float MountainAmplitude;
        /// <summary>細部(fBm)の最初の octave と、その振幅。最後の octave まで続く。</summary>
        public int DetailOctave;
        public float DetailAmplitude;

        public static PlanetHeightSettings Default=>new PlanetHeightSettings{
            SeaLevel=0f,ShoreWidth=0.05f,OceanDepth=0.4f,PlainsHeight=0.05f,MountainOctave=3,MountainAmplitude=0.35f,DetailOctave=4,DetailAmplitude=0.08f,
        };
    }

    /// <summary>Cell ごとの、octave の格子の基点(<see cref="PlanetCellFrame.TryNoiseLattice"/>)。</summary>
    public sealed class PlanetHeightLattice{
        public readonly Vector3Int[] Lattice=new Vector3Int[PlanetHeightField.MaximumOctaves];
        public readonly Vector3[] Fraction=new Vector3[PlanetHeightField.MaximumOctaves];

        public bool TryFill(in PlanetCellFrame frame,in LogicalScalar inverseWavelength,int octaves){
            for(int octave=0;octave<octaves;octave++){
                if(!frame.TryNoiseLattice(in inverseWavelength,octave,out this.Lattice[octave],out this.Fraction[octave])){return false;}
            }
            return true;
        }
    }

    /// <summary>
    /// 手続き生成の惑星の高さの Field。GPU の PlanetHeight.hlsl と同じ式(片方を変えたらもう片方も変える)。
    /// 位置は Cell の原点からの相対位置 sphere(球面上の点)。高さ = Hmax·clamp(大陸 + 山 + 細部, −1, 1)。
    /// 帯域制限: 波長が 2·quadLength より短い octave は足さない(quadLength=0 なら全部)。
    /// 落とす octave の振幅は波長に比例して小さい(振幅÷波長が一定)ので、階層をまたぐ差は quad の数 % にとどまる。
    /// </summary>
    public static class PlanetHeightField{
        public const int MaximumOctaves=32;
        public const int ContinentOctaves=4;
        public const int MountainOctaves=6;

        public static float Evaluate(in PlanetHeightSettings settings,float heightAmplitude,float inverseWavelength,int octaves,PlanetHeightLattice lattice,Vector3 sphere,float quadLength){
            float continent=0;
            float amplitude=0.5f;
            for(int k=0;k<ContinentOctaves&&k<octaves;k++){
                if(Resolved(k,quadLength,inverseWavelength)){
                    continent+=amplitude*OctaveNoise(lattice,sphere,inverseWavelength,k,0u);
                }
                amplitude*=0.5f;
            }
            float land=SmoothStep(settings.SeaLevel-settings.ShoreWidth,settings.SeaLevel+settings.ShoreWidth,continent);
            float total=Mathf.Lerp(-settings.OceanDepth,settings.PlainsHeight,land);
            float mountains=0;
            amplitude=settings.MountainAmplitude;
            for(int m=0;m<MountainOctaves;m++){
                int octave=settings.MountainOctave+m;
                if(octave<octaves&&Resolved(octave,quadLength,inverseWavelength)){
                    float ridge=1f-Mathf.Abs(OctaveNoise(lattice,sphere,inverseWavelength,octave,1u));
                    mountains+=amplitude*(ridge*ridge-0.4f);
                }
                amplitude*=0.5f;
            }
            total+=mountains*land;
            amplitude=settings.DetailAmplitude;
            for(int d=settings.DetailOctave;d<octaves;d++){
                if(Resolved(d,quadLength,inverseWavelength)){
                    total+=amplitude*OctaveNoise(lattice,sphere,inverseWavelength,d,2u);
                }
                amplitude*=0.5f;
            }
            return Mathf.Clamp(total,-1f,1f)*heightAmplitude;
        }

        // HLSL の smoothstep と同じ(Mathf.SmoothStep は別の式)。
        private static float SmoothStep(float low,float high,float value){
            float t=Mathf.Clamp01((value-low)/(high-low));
            return t*t*(3f-2f*t);
        }

        private static bool Resolved(int octave,float quadLength,float inverseWavelength){
            return MathF.Pow(2f,-octave)>=2f*quadLength*inverseWavelength;
        }

        private static float OctaveNoise(PlanetHeightLattice lattice,Vector3 sphere,float inverseWavelength,int octave,uint channel){
            Vector3 q=lattice.Fraction[octave]+sphere*(inverseWavelength*MathF.Pow(2f,octave));
            Vector3 whole=new Vector3(Mathf.Floor(q.x),Mathf.Floor(q.y),Mathf.Floor(q.z));
            Vector3Int baseCell=lattice.Lattice[octave];
            var cell=new Vector3Int(unchecked(baseCell.x+(int)whole.x),unchecked(baseCell.y+(int)whole.y),unchecked(baseCell.z+(int)whole.z));
            return GradientNoise(cell,q-whole,channel*64u+(uint)octave);
        }

        private static float GradientNoise(Vector3Int i,Vector3 f,uint channel){
            Vector3 w=new Vector3(Fade(f.x),Fade(f.y),Fade(f.z));
            float n000=Vector3.Dot(Gradient(i.x,i.y,i.z,channel),f);
            float n100=Vector3.Dot(Gradient(i.x+1,i.y,i.z,channel),f-new Vector3(1,0,0));
            float n010=Vector3.Dot(Gradient(i.x,i.y+1,i.z,channel),f-new Vector3(0,1,0));
            float n110=Vector3.Dot(Gradient(i.x+1,i.y+1,i.z,channel),f-new Vector3(1,1,0));
            float n001=Vector3.Dot(Gradient(i.x,i.y,i.z+1,channel),f-new Vector3(0,0,1));
            float n101=Vector3.Dot(Gradient(i.x+1,i.y,i.z+1,channel),f-new Vector3(1,0,1));
            float n011=Vector3.Dot(Gradient(i.x,i.y+1,i.z+1,channel),f-new Vector3(0,1,1));
            float n111=Vector3.Dot(Gradient(i.x+1,i.y+1,i.z+1,channel),f-new Vector3(1,1,1));
            float x00=Lerp(n000,n100,w.x);float x10=Lerp(n010,n110,w.x);
            float x01=Lerp(n001,n101,w.x);float x11=Lerp(n011,n111,w.x);
            return Lerp(Lerp(x00,x10,w.y),Lerp(x01,x11,w.y),w.z)*1.5f;
        }

        // HLSL の lerp と同じ(clamp しない)。
        private static float Lerp(float a,float b,float t){
            return a+(b-a)*t;
        }

        private static float Fade(float f){
            return f*f*f*(f*(f*6f-15f)+10f);
        }

        private static Vector3 Gradient(int x,int y,int z,uint channel){
            uint vx=unchecked((uint)x+channel*0x9e3779b9u);
            uint vy=unchecked((uint)y+channel*0x85ebca6bu);
            uint vz=unchecked((uint)z+channel*0xc2b2ae35u);
            unchecked{
                vx=vx*1664525u+1013904223u;vy=vy*1664525u+1013904223u;vz=vz*1664525u+1013904223u;
                vx+=vy*vz;vy+=vz*vx;vz+=vx*vy;
                vx^=vx>>16;vy^=vy>>16;vz^=vz>>16;
                vx+=vy*vz;vy+=vz*vx;vz+=vx*vy;
            }
            var g=new Vector3((vx&0xffffu)/32767.5f-1f+1e-6f,(vy&0xffffu)/32767.5f-1f+1e-6f,(vz&0xffffu)/32767.5f-1f+1e-6f);
            return g/g.magnitude;
        }
    }
}
