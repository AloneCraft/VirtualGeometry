using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace VirtualGeometry.Omni.Tests{
    public sealed class MoonTripleDribbleTests{
        // 物理エンジンで、frames フレーム(1 フレームは seconds 秒)進めて、ボールの中心の高さの最小・最大と、跳ね返った瞬間の最高の高さ(跳ね返りの前の最高点)を集める。
        private static void Run(MoonTripleDribble dribble,double seconds,int frames,double ground,out double minimum,out double maximum,List<double> apexes){
            minimum=double.MaxValue;
            maximum=double.MinValue;
            double lastVelocity=dribble.Velocity;
            for(int i=0;i<frames;i++){
                dribble.Step(seconds,ground);
                double height=dribble.Height;
                minimum=Math.Min(minimum,height);
                maximum=Math.Max(maximum,height);
                Assert.That(dribble.Position.x,Is.EqualTo(0f),"x は動かない");
                Assert.That(dribble.Position.z,Is.EqualTo(0f),"z は動かない");
                double velocity=dribble.Velocity;
                if(apexes!=null&&lastVelocity>0&&velocity<=0){apexes.Add(height);}
                lastVelocity=velocity;
            }
        }

        [Test]
        public void Start(){
            {
                //地面の高さから、跳ね返った直後の状態(上向きの初速)で始まる。x と z は 0
                using(var dribble=new MoonTripleDribble()){
                    dribble.Start(0.25);
                    Assert.That(dribble.Running,Is.True);
                    Assert.That(dribble.Height,Is.EqualTo(0.25).Within(1e-6));
                    Assert.That(dribble.Velocity,Is.EqualTo(MoonTripleDribble.LaunchSpeed).Within(1e-4));
                    Assert.That(dribble.Position.x,Is.EqualTo(0f));
                    Assert.That(dribble.Position.z,Is.EqualTo(0f));
                }
            }
            {
                //初速と 1 往復の時間は、プロジェクトの重力(Physics.gravity)から決まる: v = √(2·g·1 m)、T = 2v/g
                Assert.That(MoonTripleDribble.Gravity,Is.EqualTo(Math.Abs(Physics.gravity.y)));
                Assert.That(MoonTripleDribble.LaunchSpeed,Is.EqualTo(Math.Sqrt(2*MoonTripleDribble.Gravity)).Within(1e-9));
                Assert.That(MoonTripleDribble.Period,Is.EqualTo(2*MoonTripleDribble.LaunchSpeed/MoonTripleDribble.Gravity).Within(1e-9));
            }
        }

        [Test]
        public void Stop(){
            {
                //止めると、時間が進んでも動かない
                using(var dribble=new MoonTripleDribble()){
                    dribble.Start(0);
                    dribble.Step(0.1,0);
                    double height=dribble.Height;
                    dribble.Stop();
                    dribble.Step(0.3,0);
                    Assert.That(dribble.Running,Is.False);
                    Assert.That(dribble.Height,Is.EqualTo(height));
                }
            }
        }

        [Test]
        public void Step(){
            {
                //始めていないときは、動かず、物理シーンも作らない
                using(var dribble=new MoonTripleDribble()){
                    dribble.Step(0.5,0);
                    Assert.That(dribble.Height,Is.EqualTo(0.0));
                    Assert.That(dribble.Running,Is.False);
                }
            }
            {
                //地面 0 で 20 秒(60 fps)。最高の高さは毎回 1 m(1 cm 以内。エネルギーが増えて、だんだん高くならない)、ボールの中心は地面の下に 1 cm 以上入らない
                using(var dribble=new MoonTripleDribble()){
                    dribble.Start(0);
                    var apexes=new List<double>();
                    Run(dribble,1.0/60.0,60*20,0,out double minimum,out double maximum,apexes);
                    Assert.That(apexes.Count,Is.GreaterThanOrEqualTo(20));
                    foreach(double apex in apexes){Assert.That(apex,Is.EqualTo(1.0).Within(0.01));}
                    Assert.That(maximum,Is.LessThanOrEqualTo(1.0+0.01));
                    Assert.That(minimum,Is.GreaterThanOrEqualTo(-0.01));
                }
            }
            {
                //フレームの間隔が 8 ms でも 33 ms でも、結果は同じ(1 cm 以内)。1 回の更新が長い(1000 秒)ときは、上限の時間だけ進んで、壊れない
                foreach(double seconds in new[]{1.0/120.0,1.0/30.0,1000.0}){
                    using(var dribble=new MoonTripleDribble()){
                        dribble.Start(0);
                        Run(dribble,seconds,seconds>1?3:60*10,0,out double minimum,out double maximum,null);
                        Assert.That(maximum,Is.LessThanOrEqualTo(1.0+0.01),"間隔 "+seconds+" 秒");
                        Assert.That(minimum,Is.GreaterThanOrEqualTo(-0.01),"間隔 "+seconds+" 秒");
                    }
                }
            }
            {
                //飛んでいる途中で面が 10 mm 上がる: ボールは面の下に 1 cm 以上入らず、その後の最高点は「新しい面 + 1 m」
                using(var dribble=new MoonTripleDribble()){
                    dribble.Start(0);
                    Run(dribble,1.0/60.0,15,0,out _,out _,null);
                    var apexes=new List<double>();
                    Run(dribble,1.0/60.0,60*10,0.01,out double minimum,out _,apexes);
                    Assert.That(minimum,Is.GreaterThanOrEqualTo(0.01-0.01));
                    Assert.That(apexes.Count,Is.GreaterThanOrEqualTo(5));
                    for(int i=1;i<apexes.Count;i++){Assert.That(apexes[i],Is.EqualTo(1.01).Within(0.01));}
                }
            }
            {
                //面が 10 mm 下がる: ボールはそのまま落ちて、新しい面の高さまで下がり、その後の最高点は「新しい面 + 1 m」
                using(var dribble=new MoonTripleDribble()){
                    dribble.Start(0);
                    Run(dribble,1.0/60.0,45,0,out _,out _,null);
                    var apexes=new List<double>();
                    //下がった 10 mm を測るため、標本を細かく(1/960 秒。ボールの速さ 4.4 m/s で 4.6 mm 刻み)取る
                    Run(dribble,1.0/960.0,960*6,-0.01,out double minimum,out _,apexes);
                    Assert.That(minimum,Is.LessThan(-0.005),"面が下がったので、元の面の高さより下まで落ちる");
                    Assert.That(minimum,Is.GreaterThanOrEqualTo(-0.01-0.01));
                    Assert.That(apexes.Count,Is.GreaterThanOrEqualTo(5));
                    for(int i=1;i<apexes.Count;i++){Assert.That(apexes[i],Is.EqualTo(0.99).Within(0.01));}
                }
            }
        }

        [Test]
        public void Dispose(){
            {
                //片付けたあとは、動かさず、壊れない
                var dribble=new MoonTripleDribble();
                dribble.Start(0);
                dribble.Dispose();
                Assert.That(dribble.Running,Is.False);
                Assert.DoesNotThrow(()=>dribble.Step(0.1,0));
                Assert.That(dribble.Height,Is.EqualTo(0.0));
            }
        }
    }
}
