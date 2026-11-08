namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::Timer's easing curves (static float f(float rate)): the rate is clamped to 0..1 first (1 and above give 1, 0
/// and below 0), then the classic in/out/in-out families are applied (quad .. quint powers, sine, exponential,
/// circular, back with overshoot 1.70158, elastic with period 0.3, bounce with the 7.5625 parabola segments).
/// </summary>
public sealed partial class Paf
{
    static float Ease(float t, Func<float, float> f) => t >= 1 ? 1 : t <= 0 ? 0 : f(t);
    static float InOut(float t, Func<float, float> easeIn) => Ease(t, x => x < 0.5f ? easeIn(x * 2) * 0.5f : 1 - easeIn((1 - x) * 2) * 0.5f);
    static float Out(float t, Func<float, float> easeIn) => Ease(t, x => 1 - easeIn(1 - x));

    static float PowIn(float x, int n) { float r = x; for (int i = 1; i < n; i++) r *= x; return r; }
    static float SineInF(float x) => 1 - MathF.Cos(x * MathF.PI * 0.5f);
    static float ExpoInF(float x) => x <= 0 ? 0 : MathF.Pow(2, 10 * (x - 1));
    static float CirqInF(float x) => 1 - MathF.Sqrt(1 - x * x);
    static float BackInF(float x) => x * x * (2.70158f * x - 1.70158f);
    static float ElasticInF(float x) => x <= 0 ? 0 : x >= 1 ? 1 : -MathF.Pow(2, 10 * (x - 1)) * MathF.Sin((x - 1.075f) * 2 * MathF.PI / 0.3f);
    static float BounceOutF(float x)
    {
        if (x < 1 / 2.75f) return 7.5625f * x * x;
        if (x < 2 / 2.75f) { x -= 1.5f / 2.75f; return 7.5625f * x * x + 0.75f; }
        if (x < 2.5f / 2.75f) { x -= 2.25f / 2.75f; return 7.5625f * x * x + 0.9375f; }
        x -= 2.625f / 2.75f;
        return 7.5625f * x * x + 0.984375f;
    }
    static float BounceInF(float x) => 1 - BounceOutF(1 - x);

    [Hle("_ZN3paf5Timer12DoFuncLinearEf")] float EaseLinear(float t) => Ease(t, x => x);
    [Hle("_ZN3paf5Timer12DoFuncQuadInEf")] float EaseQuadIn(float t) => Ease(t, x => PowIn(x, 2));
    [Hle("_ZN3paf5Timer13DoFuncQuadOutEf")] float EaseQuadOut(float t) => Out(t, x => PowIn(x, 2));
    [Hle("_ZN3paf5Timer15DoFuncQuadInOutEf")] float EaseQuadInOut(float t) => InOut(t, x => PowIn(x, 2));
    [Hle("_ZN3paf5Timer13DoFuncCubicInEf")] float EaseCubicIn(float t) => Ease(t, x => PowIn(x, 3));
    [Hle("_ZN3paf5Timer14DoFuncCubicOutEf")] float EaseCubicOut(float t) => Out(t, x => PowIn(x, 3));
    [Hle("_ZN3paf5Timer16DoFuncCubicInOutEf")] float EaseCubicInOut(float t) => InOut(t, x => PowIn(x, 3));
    [Hle("_ZN3paf5Timer13DoFuncQuartInEf")] float EaseQuartIn(float t) => Ease(t, x => PowIn(x, 4));
    [Hle("_ZN3paf5Timer14DoFuncQuartOutEf")] float EaseQuartOut(float t) => Out(t, x => PowIn(x, 4));
    [Hle("_ZN3paf5Timer16DoFuncQuartInOutEf")] float EaseQuartInOut(float t) => InOut(t, x => PowIn(x, 4));
    [Hle("_ZN3paf5Timer13DoFuncQuintInEf")] float EaseQuintIn(float t) => Ease(t, x => PowIn(x, 5));
    [Hle("_ZN3paf5Timer14DoFuncQuintOutEf")] float EaseQuintOut(float t) => Out(t, x => PowIn(x, 5));
    [Hle("_ZN3paf5Timer16DoFuncQuintInOutEf")] float EaseQuintInOut(float t) => InOut(t, x => PowIn(x, 5));
    [Hle("_ZN3paf5Timer12DoFuncSineInEf")] float EaseSineIn(float t) => Ease(t, SineInF);
    [Hle("_ZN3paf5Timer13DoFuncSineOutEf")] float EaseSineOut(float t) => Out(t, SineInF);
    [Hle("_ZN3paf5Timer15DoFuncSineInOutEf")] float EaseSineInOut(float t) => InOut(t, SineInF);
    [Hle("_ZN3paf5Timer12DoFuncExpoInEf")] float EaseExpoIn(float t) => Ease(t, ExpoInF);
    [Hle("_ZN3paf5Timer13DoFuncExpoOutEf")] float EaseExpoOut(float t) => Out(t, ExpoInF);
    [Hle("_ZN3paf5Timer15DoFuncExpoInOutEf")] float EaseExpoInOut(float t) => InOut(t, ExpoInF);
    [Hle("_ZN3paf5Timer12DoFuncCirqInEf")] float EaseCirqIn(float t) => Ease(t, CirqInF);
    [Hle("_ZN3paf5Timer13DoFuncCirqOutEf")] float EaseCirqOut(float t) => Out(t, CirqInF);
    [Hle("_ZN3paf5Timer15DoFuncCirqInOutEf")] float EaseCirqInOut(float t) => InOut(t, CirqInF);
    [Hle("_ZN3paf5Timer12DoFuncBackInEf")] float EaseBackIn(float t) => Ease(t, BackInF);
    [Hle("_ZN3paf5Timer13DoFuncBackOutEf")] float EaseBackOut(float t) => Out(t, BackInF);
    [Hle("_ZN3paf5Timer15DoFuncBackInOutEf")] float EaseBackInOut(float t) => InOut(t, BackInF);
    [Hle("_ZN3paf5Timer15DoFuncElasticInEf")] float EaseElasticIn(float t) => Ease(t, ElasticInF);
    [Hle("_ZN3paf5Timer16DoFuncElasticOutEf")] float EaseElasticOut(float t) => Out(t, ElasticInF);
    [Hle("_ZN3paf5Timer18DoFuncElasticInOutEf")] float EaseElasticInOut(float t) => InOut(t, ElasticInF);
    [Hle("_ZN3paf5Timer14DoFuncBounceInEf")] float EaseBounceIn(float t) => Ease(t, BounceInF);
    [Hle("_ZN3paf5Timer15DoFuncBounceOutEf")] float EaseBounceOut(float t) => Ease(t, BounceOutF);
    [Hle("_ZN3paf5Timer17DoFuncBounceInOutEf")] float EaseBounceInOut(float t) => InOut(t, BounceInF);
}
