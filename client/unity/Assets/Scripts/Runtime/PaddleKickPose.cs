using UnityEngine;

public enum PaddleKick { Front, Side, Wide }

/// The paddle kick as pure maths, with no scene dependencies. Each leg is a hip joint and a
/// knee joint. The knee swings 90 -> 0 -> 90 degrees over one unit of kick time, and the kick
/// type only sets how far the hip turns outward. The last quarter blends back to the rest pose.
public static class PaddleKickPose
{
    public const float Duration   = 1f;     // kick time; PaddleKicker.animationSpeed scales it
    public const float BlendStart = 0.75f;  // start of the blend back to rest

    const float HipPitch      = -90f;
    const float KneeRestPitch = 90f;

    public static readonly Quaternion HipRest  = Quaternion.Euler(HipPitch, 0f, 0f);
    public static readonly Quaternion KneeRest = Quaternion.Euler(KneeRestPitch, 0f, 0f);

    public static float HipYaw(PaddleKick kick)
    {
        switch (kick)
        {
            case PaddleKick.Side: return 20f;
            case PaddleKick.Wide: return 70f;
            default:              return 0f;
        }
    }

    /// Knee angle in degrees at kick time t: eased out to straight at the midpoint, then back.
    public static float KneePitch(float t)
    {
        float half = Duration * 0.5f;
        return t < half
            ? KneeRestPitch * (1f - SmoothStep(t / half))
            : KneeRestPitch * SmoothStep((t - half) / half);
    }

    /// Joint rotations at kick time t. side: -1 for the left leg, +1 for the right.
    public static void Evaluate(PaddleKick kick, float t, float side, out Quaternion hip, out Quaternion knee)
    {
        hip  = Quaternion.Euler(HipPitch, side * HipYaw(kick), 0f);
        knee = Quaternion.Euler(KneePitch(t), 0f, 0f);

        float blend = Mathf.Clamp01((t - BlendStart) / (Duration - BlendStart));
        hip  = Quaternion.Slerp(hip, HipRest, blend);   // slerp, as the browser preview blends
        knee = Quaternion.Slerp(knee, KneeRest, blend);
    }

    static float SmoothStep(float u) => u * u * (3f - 2f * u);
}
